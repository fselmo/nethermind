// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Ethereum.Test.Base;

/// <summary>
/// The blockchain fixture's <c>blockAccessList</c> entry: the EIP-7928 list
/// travels beside the block RLP, and a block decoded from the RLP alone has
/// none, so the parallel processor never runs on a test block unless the
/// fixture's list is attached.
/// </summary>
public class BlockAccessListAccountJson
{
    public string? Address { get; set; }
    public BlockAccessListNonceJson[]? NonceChanges { get; set; }
    public BlockAccessListBalanceJson[]? BalanceChanges { get; set; }
    public BlockAccessListCodeJson[]? CodeChanges { get; set; }
    public BlockAccessListSlotJson[]? StorageChanges { get; set; }
    public string[]? StorageReads { get; set; }

    public static ReadOnlyBlockAccessList ToBlockAccessList(BlockAccessListAccountJson[] accounts)
    {
        ReadOnlyAccountChanges[] changes = new ReadOnlyAccountChanges[accounts.Length];
        int itemCount = 0;
        for (int i = 0; i < accounts.Length; i++)
        {
            BlockAccessListAccountJson a = accounts[i];
            ReadOnlySlotChanges[] storageChanges = (a.StorageChanges ?? []).Select(s =>
                new ReadOnlySlotChanges(
                    ToUInt256(s.Slot),
                    (s.SlotChanges ?? []).Select(c => new StorageChange(ToIndex(c.BlockAccessIndex), ToUInt256(c.PostValue).ToBigEndianWord())).ToArray()))
                .ToArray();
            UInt256[] storageReads = (a.StorageReads ?? []).Select(ToUInt256).ToArray();
            changes[i] = new ReadOnlyAccountChanges(
                new Address(a.Address!),
                storageChanges,
                storageReads,
                (a.BalanceChanges ?? []).Select(c => new BalanceChange(ToIndex(c.BlockAccessIndex), ToUInt256(c.PostBalance))).ToArray(),
                (a.NonceChanges ?? []).Select(c => new NonceChange(ToIndex(c.BlockAccessIndex), ToULong(c.PostNonce))).ToArray(),
                (a.CodeChanges ?? []).Select(c => new CodeChange(ToIndex(c.BlockAccessIndex), Bytes.FromHexString(c.NewCode!))).ToArray());
            itemCount += 1 + storageChanges.Length + storageReads.Length;
        }
        return new ReadOnlyBlockAccessList(changes, itemCount);
    }

    private static UInt256 ToUInt256(string? hex) => Bytes.FromHexString(hex!).ToUInt256();
    private static ulong ToULong(string? hex) => Bytes.FromHexString(hex!).ToULongFromBigEndianByteArrayWithoutLeadingZeros();
    private static uint ToIndex(string? hex) => (uint)ToULong(hex);
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
