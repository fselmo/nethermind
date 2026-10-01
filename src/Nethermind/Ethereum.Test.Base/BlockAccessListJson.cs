// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Ethereum.Test.Base;

/// <summary>
/// One account of a blockchain fixture's EIP-7928 <c>blockAccessList</c>, which travels beside the block RLP.
/// </summary>
public class BlockAccessListAccountJson
{
    public string? Address { get; set; }
    public BlockAccessListNonceJson[]? NonceChanges { get; set; }
    public BlockAccessListBalanceJson[]? BalanceChanges { get; set; }
    public BlockAccessListCodeJson[]? CodeChanges { get; set; }
    public BlockAccessListSlotJson[]? StorageChanges { get; set; }
    public string[]? StorageReads { get; set; }

    /// <summary>Converts the accounts in fixture order, so a list the fixture corrupted stays corrupted.</summary>
    public static ReadOnlyBlockAccessList ToBlockAccessList(BlockAccessListAccountJson[] accounts)
    {
        ReadOnlyAccountChanges[] changes = new ReadOnlyAccountChanges[accounts.Length];
        int itemCount = 0;
        for (int i = 0; i < accounts.Length; i++)
        {
            BlockAccessListAccountJson account = accounts[i];
            ReadOnlySlotChanges[] storageChanges = (account.StorageChanges ?? [])
                .Select(static s => new ReadOnlySlotChanges(
                    ToUInt256(s.Slot),
                    (s.SlotChanges ?? []).Select(static c => new StorageChange(ToIndex(c.BlockAccessIndex), ToUInt256(c.PostValue))).ToArray()))
                .ToArray();
            UInt256[] storageReads = (account.StorageReads ?? []).Select(ToUInt256).ToArray();
            changes[i] = new ReadOnlyAccountChanges(
                new Address(account.Address!),
                storageChanges,
                storageReads,
                (account.BalanceChanges ?? []).Select(static c => new BalanceChange(ToIndex(c.BlockAccessIndex), ToUInt256(c.PostBalance))).ToArray(),
                (account.NonceChanges ?? []).Select(static c => new NonceChange(ToIndex(c.BlockAccessIndex), ToULong(c.PostNonce))).ToArray(),
                (account.CodeChanges ?? []).Select(static c => new CodeChange(ToIndex(c.BlockAccessIndex), Bytes.FromHexString(c.NewCode!))).ToArray());
            itemCount += 1 + storageChanges.Length + storageReads.Length;
        }

        return new ReadOnlyBlockAccessList(changes, itemCount);
    }

    private static UInt256 ToUInt256(string? hex) => Bytes.FromHexString(hex!).ToUInt256();
    private static ulong ToULong(string? hex) => Bytes.FromHexString(hex!).ToULongFromBigEndianByteArrayWithoutLeadingZeros();
    private static uint ToIndex(string? hex) => checked((uint)ToULong(hex));
}

public class BlockAccessListNonceJson
{
    public string? BlockAccessIndex { get; set; }
    public string? PostNonce { get; set; }
}

public class BlockAccessListBalanceJson
{
    public string? BlockAccessIndex { get; set; }
    public string? PostBalance { get; set; }
}

public class BlockAccessListCodeJson
{
    public string? BlockAccessIndex { get; set; }
    public string? NewCode { get; set; }
}

public class BlockAccessListSlotJson
{
    public string? Slot { get; set; }
    public BlockAccessListStorageChangeJson[]? SlotChanges { get; set; }
}

public class BlockAccessListStorageChangeJson
{
    public string? BlockAccessIndex { get; set; }
    public string? PostValue { get; set; }
}
