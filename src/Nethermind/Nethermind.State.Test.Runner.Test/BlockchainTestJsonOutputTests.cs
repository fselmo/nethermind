// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.State.Test.Runner.Test;

/// <summary>
/// Stdout carries nethtest's results document, so anything else written there corrupts it.
/// </summary>
/// <remarks>
/// The workflow parses the captured stdout with <c>jq</c>; when that fails it falls back to a
/// stderr-derived summary that has no per-error grouping and reports the run as a crash. The
/// post-state comparison writes its truncation notice to <see cref="Console.Out"/> once a test has
/// gathered more than eight differences, which used to corrupt the results artifact of every run
/// containing such a test. The runner is driven as a process here because the assertions inside
/// it are NUnit ones, and those mark the surrounding test failed even when the runner catches them.
/// </remarks>
[TestFixture]
public class BlockchainTestJsonOutputTests
{
    /// <summary>Enough mismatching accounts to carry the comparison past its eight-difference truncation threshold.</summary>
    private const int MismatchingAccounts = 12;

    private static readonly TimeSpan NethtestTimeout = TimeSpan.FromMinutes(2);

    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, true);

    [Test]
    public async Task Stdout_stays_parseable_json_when_a_test_reports_more_than_8_differences()
    {
        (string stdout, string stderr) = await RunNethtest(WriteFixture());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stderr, Does.Contain("More than 8 differences"),
                "the fixture has to reach the truncation branch, otherwise this test proves nothing");
            Assert.That(() => JsonDocument.Parse(stdout).Dispose(), Throws.Nothing,
                $"stdout must stay a parseable results document, was: {Trim(stdout)}");
            Assert.That(ResultCount(stdout), Is.EqualTo(1));
        }
    }

    [Test]
    public async Task Block_tests_run_under_the_standard_command_name_and_the_option([Values("blocktest", "--blockTest")] string testType)
    {
        (string stdout, _) = await RunNethtest(WriteFixture(), testType);

        Assert.That(ResultCount(stdout), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
    }

    [Test]
    public async Task Bal_report_prints_one_execution_line_per_block([Values("blocktest", "--blockTest")] string testType)
    {
        (string fixture, string blockHash) = WriteOneBlockFixture();
        (string stdout, string stderr) = await RunNethtest(fixture, testType, "--bal-report");

        Assert.That(ResultCount(stdout), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
        Assert.That(EventLines(stderr), Is.EqualTo(new[]
        {
            $$"""{"event":"balExecution","block":1,"hash":"{{blockHash}}","path":"sequential","reason":"pre-amsterdam"}""",
        }), $"stderr was: {Trim(stderr)}");
    }

    [Test]
    public async Task Without_bal_report_no_event_line_is_printed()
    {
        (string stdout, string stderr) = await RunNethtest(WriteOneBlockFixture().Path, "blocktest");

        Assert.That(ResultCount(stdout), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
        Assert.That(EventLines(stderr), Is.Empty, $"stderr was: {Trim(stderr)}");
    }

    [Test]
    public async Task Every_path_argument_runs_in_one_results_array([Values("blocktest", "enginetest")] string command)
    {
        string first = WriteFixture();
        string second = WriteOneBlockFixture().Path;
        (string stdout, string stderr) = await RunNethtestWithArgs(command, first, second, "--jsonout", "--neverTrace");

        Assert.That(ResultNames(stdout), Is.EquivalentTo(new[] { "more_than_8_differences", "one_block" }), $"stderr was: {Trim(stderr)}");
    }

    [Test]
    public async Task Input_option_runs_together_with_path_arguments()
    {
        string directory = Path.Combine(_directory, "directory");
        Directory.CreateDirectory(directory);
        string inDirectory = Path.Combine(directory, "one_block.json");
        File.Move(WriteOneBlockFixture().Path, inDirectory);
        (string stdout, string stderr) = await RunNethtestWithArgs("blocktest", "--input", WriteFixture(), directory, "--jsonout", "--neverTrace");

        Assert.That(ResultNames(stdout), Is.EquivalentTo(new[] { "more_than_8_differences", "one_block" }), $"stderr was: {Trim(stderr)}");
    }

    [Test]
    public async Task A_missing_path_is_an_error_before_anything_runs([Values] bool viaInputOption)
    {
        string missing = Path.Combine(_directory, "missing.json");
        string[] args = viaInputOption
            ? ["blocktest", WriteFixture(), "--input", missing, "--jsonout", "--neverTrace"]
            : ["blocktest", WriteFixture(), missing, "--jsonout", "--neverTrace"];
        (string stdout, string stderr, int exitCode) = await RunNethtestProcess(args);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exitCode, Is.Not.Zero);
            Assert.That(stdout, Is.Empty, "no fixture may run when any path is missing");
            Assert.That(stderr, Does.Contain(missing));
        }
    }

    private static string[] ResultNames(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        string[] names = new string[document.RootElement.GetArrayLength()];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = document.RootElement[i].GetProperty("name").GetString()!;
        }

        return names;
    }

    private static string[] EventLines(string stderr) =>
        Array.FindAll(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries), static line => line.StartsWith("{\"event\""));

    private static int ResultCount(string stdout)
    {
        using JsonDocument document = JsonDocument.Parse(stdout);
        return document.RootElement.GetArrayLength();
    }

    private static string Trim(string output) => output.Length <= 200 ? output : $"{output[..200]}...";

    /// <summary>Runs the built nethtest binary over a fixture the way the nethtest workflow does.</summary>
    private static Task<(string Stdout, string Stderr)> RunNethtest(string fixture, string testType = "--blockTest", params string[] extraArgs) =>
        RunNethtestWithArgs([testType, "--input", fixture, "--jsonout", "--neverTrace", .. extraArgs]);

    private static async Task<(string Stdout, string Stderr)> RunNethtestWithArgs(params string[] args)
    {
        (string stdout, string stderr, _) = await RunNethtestProcess(args);
        return (stdout, stderr);
    }

    private static async Task<(string Stdout, string Stderr, int ExitCode)> RunNethtestProcess(string[] args)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "nethtest.exe" : "nethtest");
        Assert.That(File.Exists(executable), $"nethtest was not built next to the tests at {executable}");

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        // Both streams are drained before waiting, so neither can fill its buffer and block the run.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(NethtestTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"nethtest did not exit within {NethtestTimeout}");
        }

        return (await stdout, await stderr, process.ExitCode);
    }

    /// <summary>
    /// Writes a fixture whose post-state expects accounts that its empty pre-state never creates, so
    /// every one of them is a difference. It declares no blocks, which keeps the head at genesis and
    /// the comparison independent of block execution.
    /// </summary>
    private string WriteFixture()
    {
        TestBlockHeaderJson genesis = GenesisHeader();
        string file = Path.Combine(_directory, "more_than_8_differences.json");
        File.WriteAllText(file, $$"""
            {
              "more_than_8_differences": {
                "network": "Berlin",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "blocks": [],
                "lastblockhash": "{{genesis.Hash}}",
                "pre": {},
                "postState": {{MismatchingPostState()}}
              }
            }
            """);

        return file;
    }

    /// <summary>Writes a passing Paris fixture with one empty block, which the runner executes and so reports.</summary>
    private (string Path, string BlockHash) WriteOneBlockFixture()
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        byte[] blockRlp = Rlp.Encode(new Block(JsonToEthereumTest.Convert(ChildHeader(genesis, "0x01")))).Bytes;
        string blockHash = Rlp.Decode<Block>(blockRlp).Header.Hash!.ToString();

        string file = Path.Combine(_directory, "one_block.json");
        File.WriteAllText(file, $$"""
            {
              "one_block": {
                "network": "Paris",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "blocks": [{ "rlp": "{{blockRlp.ToHexString(true)}}" }],
                "lastblockhash": "{{blockHash}}",
                "pre": {},
                "postState": {}
              }
            }
            """);

        return (file, blockHash);
    }

    /// <summary>A post-merge Paris child of <paramref name="parent"/> with the given block number.</summary>
    private static TestBlockHeaderJson ChildHeader(TestBlockHeaderJson parent, string number)
    {
        TestBlockHeaderJson child = GenesisHeader();
        child.Difficulty = "0x00";
        child.Number = number;
        child.ParentHash = parent.Hash;
        child.Timestamp = "0x0c";
        child.BaseFeePerGas = BaseFeeCalculator.Calculate(JsonToEthereumTest.Convert(parent), Paris.Instance).ToHexString(true);
        return child;
    }

    private static string MismatchingPostState()
    {
        StringBuilder postState = new("{");
        for (int i = 1; i <= MismatchingAccounts; i++)
        {
            postState
                .Append(i == 1 ? "" : ",")
                .Append($$"""
                    "0x{{i:x40}}": { "balance": "0x01", "code": "0x", "nonce": "0x00", "storage": {} }
                    """);
        }

        return postState.Append('}').ToString();
    }

    private static TestBlockHeaderJson GenesisHeader(string baseFeePerGas = null)
    {
        TestBlockHeaderJson header = new()
        {
            Bloom = Bloom.Empty.Bytes.ToHexString(true),
            Coinbase = Address.Zero.ToString(),
            Difficulty = "0x020000",
            ExtraData = "0x",
            GasLimit = "0x0f4240",
            GasUsed = "0x00",
            Hash = Keccak.Zero.ToString(),
            MixHash = Keccak.Zero.ToString(),
            Nonce = "0x0000000000000000",
            Number = "0x00",
            ParentHash = Keccak.Zero.ToString(),
            ReceiptTrie = Keccak.EmptyTreeHash.ToString(),
            StateRoot = Keccak.EmptyTreeHash.ToString(),
            Timestamp = "0x00",
            TransactionsTrie = Keccak.EmptyTreeHash.ToString(),
            UncleHash = Keccak.OfAnEmptySequenceRlp.ToString(),
            BaseFeePerGas = baseFeePerGas
        };

        // The runner rejects a genesis header whose declared hash is not the one it derives, so take
        // the hash from the same encode/decode round trip it uses.
        header.Hash = Rlp.Decode<Block>(Rlp.Encode(new Block(JsonToEthereumTest.Convert(header))).Bytes).Header.Hash!.ToString();
        return header;
    }
}
