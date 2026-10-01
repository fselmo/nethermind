// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Told which executor runs each block's transactions, and when a block the parallel executor rejected is
/// re-run sequentially. A node registers none; test runners do, to report how each fixture block was judged.
/// </summary>
public interface IBlockAccessListExecutionObserver
{
    /// <param name="block">The block about to be executed.</param>
    /// <param name="sequentialReason">
    /// Null when the parallel executor runs the block; otherwise the first condition, in the order
    /// <see cref="BlockAccessListManager.PrepareForProcessing"/> checks them, that ruled it out.
    /// </param>
    void OnExecutionPathChosen(Block block, string? sequentialReason);

    /// <param name="block">The block the parallel executor rejected.</param>
    /// <param name="parallelError">Why the parallel executor rejected it.</param>
    /// <param name="sequentialError">Why the sequential re-run rejected it, or null when it accepted the block.</param>
    void OnSequentialRetry(Block block, Exception parallelError, Exception? sequentialError);
}
