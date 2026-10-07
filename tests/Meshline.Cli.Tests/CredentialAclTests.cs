using Meshline.Cli;
using System.Buffers.Binary;

namespace Meshline.Cli.Tests;

public sealed class CredentialAclTests
{
    const uint CurrentUser = 1000;
    const uint Undefined = uint.MaxValue;

    [Theory]
    [InlineData(2, CurrentUser, 4, 4, true)] // systemd: named service user can read.
    [InlineData(2, 0u, 4, 4, true)] // The superuser already has access.
    [InlineData(2, 1001u, 4, 4, false)]
    [InlineData(2, 1001u, 2, 6, false)] // Another user must not be able to replace key bytes.
    [InlineData(8, 1000u, 4, 4, false)] // A named group is not a named user.
    [InlineData(8, 1000u, 4, 0, true)] // Masked entries grant no effective access.
    public void Named_acl_entries_obey_identity_and_mask(ushort tag, uint id, ushort permissions, ushort mask, bool expected)
    {
        var acl = Encode((1, 4, Undefined), (tag, permissions, id), (4, 0, Undefined), (16, mask, Undefined), (32, 0, Undefined));
        Assert.Equal(expected, PrivateFiles.IsPrivateLinuxAcl(acl, CurrentUser));
    }

    [Theory]
    [InlineData(4, 0)]
    [InlineData(0, 4)]
    [InlineData(2, 0)]
    public void Service_user_acl_does_not_override_group_or_other_access(ushort group, ushort other)
    {
        var acl = Encode((1, 4, Undefined), (2, 4, CurrentUser), (4, group, Undefined), (16, 7, Undefined), (32, other, Undefined));
        Assert.False(PrivateFiles.IsPrivateLinuxAcl(acl, CurrentUser));
    }

    [Fact]
    public void Invalid_acl_encoding_is_rejected()
    {
        Assert.False(PrivateFiles.IsPrivateLinuxAcl([], CurrentUser));
        Assert.False(PrivateFiles.IsPrivateLinuxAcl([2, 0, 0, 0], CurrentUser));
        Assert.False(PrivateFiles.IsPrivateLinuxAcl(new byte[5], CurrentUser));
        var acl = Encode((1, 4, Undefined), (4, 0, Undefined), (32, 0, Undefined));
        acl[0] = 3;
        Assert.False(PrivateFiles.IsPrivateLinuxAcl(acl, CurrentUser));
    }

    static byte[] Encode(params (ushort Tag, ushort Permissions, uint Id)[] entries)
    {
        var bytes = new byte[4 + entries.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = bytes.AsSpan(4 + i * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, entries[i].Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], entries[i].Permissions);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], entries[i].Id);
        }
        return bytes;
    }
}
