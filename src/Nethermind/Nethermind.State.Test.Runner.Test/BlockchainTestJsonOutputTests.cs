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

    [TestCase("blocktest", "--blockTest")]
    [TestCase("enginetest", "--engineTest")]
    [TestCase("statetest", "--stateTest")]
    public async Task Each_command_runs_the_same_tests_as_its_option(string command, string option)
    {
        string fixture = command switch
        {
            "blocktest" => WriteFixture(),
            "enginetest" => WriteEngineFixture("clean_engine", []),
            "statetest" => WriteStateFixture(),
            _ => throw new ArgumentOutOfRangeException(nameof(command))
        };
        (string commandStdout, string commandStderr) = await RunNethtest(fixture, command);
        (string optionStdout, _) = await RunNethtest(fixture, option);

        string[] names = ResultNames(commandStdout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(names, Has.Length.EqualTo(1), $"stderr was: {Trim(commandStderr)}");
            Assert.That(names, Is.EqualTo(ResultNames(optionStdout)));
        }
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

    [Test]
    public async Task A_fixture_with_no_rejected_block_reports_no_rejections([Values("blocktest", "enginetest")] string command)
    {
        string fixture = command == "blocktest" ? WriteOneBlockFixture().Path : WriteEngineFixture("clean_engine", []);
        (string stdout, string stderr) = await RunNethtest(fixture, command);

        JsonElement result = SingleResult(stdout);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stderr was: {Trim(stderr)}");
            Assert.That(result.GetProperty("rejections").GetArrayLength(), Is.Zero);
        }
    }

    // The fixture names an exception other than the one nethermind reports, so the test passes only because the
    // runner leaves the reason to the consumer.
    [Test]
    public async Task Block_test_reports_each_rejected_block_with_the_client_error_and_does_not_check_it()
    {
        (string fixture, string invalidBlockHash) = WriteRejectedBlocksFixture("TransactionException.INSUFFICIENT_ACCOUNT_FUNDS");
        (string stdout, string stderr) = await RunNethtest(fixture, "blocktest");

        JsonElement result = SingleResult(stdout);
        JsonElement rejections = result.GetProperty("rejections");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stdout was: {Trim(stdout)}, stderr was: {Trim(stderr)}");
            Assert.That(rejections.GetArrayLength(), Is.EqualTo(2), $"stdout was: {Trim(stdout)}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejections[0].GetProperty("index").GetInt32(), Is.Zero);
            Assert.That(rejections[0].TryGetProperty("hash", out _), Is.False, "a block that does not decode has no hash");
            Assert.That(rejections[0].GetProperty("error").GetString(), Is.Not.Empty);
            Assert.That(rejections[1].GetProperty("index").GetInt32(), Is.EqualTo(1));
            Assert.That(rejections[1].GetProperty("hash").GetString(), Is.EqualTo(invalidBlockHash));
            Assert.That(rejections[1].GetProperty("error").GetString(), Does.StartWith("InvalidBlockNumber: "));
        }
    }

    [Test]
    public async Task Engine_test_reports_an_invalid_payload_and_a_json_rpc_error_by_payload_index()
    {
        (string invalidPayload, string invalidBlockHash) = SkippedNumberPayload();
        string fixture = WriteEngineFixture("rejected_engine",
        [
            $$"""{ "params": [{{invalidPayload}}], "newPayloadVersion": "1", "forkchoiceUpdatedVersion": "1", "validationError": "BlockException.INVALID_BLOCK_NUMBER" }""",
            // newPayloadV3 refuses a payload without the Shanghai and Cancun fields before validating it.
            $$"""{ "params": [{{invalidPayload}}, [], "{{Keccak.Zero}}"], "newPayloadVersion": "3", "forkchoiceUpdatedVersion": "1", "errorCode": "-32602" }""",
        ]);
        (string stdout, string stderr) = await RunNethtest(fixture, "enginetest");

        JsonElement result = SingleResult(stdout);
        JsonElement rejections = result.GetProperty("rejections");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stdout was: {Trim(stdout)}, stderr was: {Trim(stderr)}");
            Assert.That(rejections.GetArrayLength(), Is.EqualTo(2), $"stdout was: {Trim(stdout)}");
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejections[0].GetProperty("index").GetInt32(), Is.Zero);
            Assert.That(rejections[0].GetProperty("hash").GetString(), Is.EqualTo(invalidBlockHash));
            Assert.That(rejections[0].GetProperty("error").GetString(), Does.StartWith("InvalidBlockNumber: "));
            Assert.That(rejections[1].GetProperty("index").GetInt32(), Is.EqualTo(1));
            Assert.That(rejections[1].TryGetProperty("hash", out _), Is.False, "a payload refused over JSON-RPC was never hashed");
            Assert.That(rejections[1].GetProperty("error").GetString(), Does.StartWith("-32602: "));
        }
    }

    // An EEST fixture whose block access list carries an entry the block never produces. The header commits to that
    // list, so it is delivered and judged during execution: the parallel executor rejects it and the sequential retry
    // rejects it again, or, with parallel execution off, the sequential executor rejects it once.
    [TestCase("blocktest", true)]
    [TestCase("blocktest", false)]
    [TestCase("enginetest", true)]
    [TestCase("enginetest", false)]
    public async Task A_corrupted_access_list_is_rejected_by_either_executor(string command, bool parallelExecution)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"bal_invalid_surplus_system_address_{command}.json");
        (string stdout, string stderr) = await RunNethtest(fixture, command, "--bal-report", "--parallelExecution", parallelExecution ? "true" : "false");

        JsonElement result = SingleResult(stdout);
        string[] events = Array.ConvertAll(EventLines(stderr), static line =>
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return root.GetProperty("event").GetString() == "balExecution"
                ? $"balExecution {root.GetProperty("path").GetString()} {root.GetProperty("reason").GetString()}".TrimEnd()
                : $"balFallback {root.GetProperty("sequentialResult").GetString()}";
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("pass").GetBoolean(), Is.True, $"stdout was: {Trim(stdout)}");
            Assert.That(result.GetProperty("rejections").GetArrayLength(), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
            Assert.That(events, Is.EqualTo(parallelExecution
                ? new[] { "balExecution parallel", "balFallback invalid" }
                : new[] { "balExecution sequential disabled" }), $"stderr was: {Trim(stderr)}");
        }

        if (command == "enginetest")
        {
            Assert.That(result.GetProperty("lastPayloadStatus").GetString(), Is.EqualTo("INVALID"));
        }
    }

    /// <summary>Parses a results array holding exactly one result.</summary>
    private static JsonElement SingleResult(string stdout)
    {
        JsonElement root = JsonDocument.Parse(stdout).RootElement;
        Assert.That(root.GetArrayLength(), Is.EqualTo(1), $"stdout was: {Trim(stdout)}");
        return root[0];
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

    /// <summary>Writes a Berlin state test with one value transfer; whether it passes does not matter here.</summary>
    private string WriteStateFixture()
    {
        string file = Path.Combine(_directory, "one_transfer.json");
        File.WriteAllText(file, $$"""
            {
              "one_transfer": {
                "env": {
                  "currentCoinbase": "0x2adc25665018aa1fe0e6bc666dac8fc2697ff9ba",
                  "currentDifficulty": "0x020000",
                  "currentGasLimit": "0x0f4240",
                  "currentNumber": "0x01",
                  "currentTimestamp": "0x03e8",
                  "previousHash": "{{Keccak.Zero}}"
                },
                "pre": {
                  "0xa94f5374fce5edbc8e2a8697c15331677e6ebf0b": { "balance": "0x0f4240", "code": "0x", "nonce": "0x00", "storage": {} }
                },
                "transaction": {
                  "secretKey": "0x45a915e4d060149eb4365960e6a7a45f334393093061116b197e3240065ff2d8",
                  "nonce": "0x00",
                  "gasPrice": "0x0a",
                  "gasLimit": ["0x5208"],
                  "to": "0x1000000000000000000000000000000000000000",
                  "value": ["0x01"],
                  "data": ["0x"]
                },
                "post": {
                  "Berlin": [
                    { "hash": "{{Keccak.Zero}}", "logs": "{{Keccak.Zero}}", "indexes": { "data": 0, "gas": 0, "value": 0 } }
                  ]
                }
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

    /// <summary>
    /// Writes a Paris fixture whose first block does not decode and whose second skips a block number, both
    /// expected to be rejected; the second names <paramref name="expectException"/>.
    /// </summary>
    private (string Path, string InvalidBlockHash) WriteRejectedBlocksFixture(string expectException)
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        // Number 2 on genesis skips a block number, which header validation rejects.
        byte[] blockRlp = Rlp.Encode(new Block(JsonToEthereumTest.Convert(ChildHeader(genesis, "0x02")))).Bytes;
        string blockHash = Rlp.Decode<Block>(blockRlp).Header.Hash!.ToString();

        string file = Path.Combine(_directory, "rejected_blocks.json");
        File.WriteAllText(file, $$"""
            {
              "rejected_blocks": {
                "network": "Paris",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "blocks": [
                  { "rlp": "0xdead", "expectException": "BlockException.RLP_STRUCTURES_ENCODING" },
                  { "rlp": "{{blockRlp.ToHexString(true)}}", "expectException": "{{expectException}}" }
                ],
                "lastblockhash": "{{genesis.Hash}}",
                "pre": {},
                "postState": {}
              }
            }
            """);

        return (file, blockHash);
    }

    /// <summary>Writes a Paris engine fixture whose head stays at genesis, sending the given payload entries.</summary>
    private string WriteEngineFixture(string name, string[] payloads)
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        genesis.Difficulty = "0x00";
        genesis.Hash = HashOf(genesis);

        string file = Path.Combine(_directory, $"{name}.json");
        File.WriteAllText(file, $$"""
            {
              "{{name}}": {
                "network": "Paris",
                "sealEngine": "NoProof",
                "genesisBlockHeader": {{_serializer.Serialize(genesis)}},
                "engineNewPayloads": [{{string.Join(",", payloads)}}],
                "lastblockhash": "{{genesis.Hash}}",
                "pre": {},
                "postState": {}
              }
            }
            """);

        return file;
    }

    /// <summary>An <c>ExecutionPayloadV1</c> on the engine fixture's genesis that skips a block number.</summary>
    private static (string Payload, string BlockHash) SkippedNumberPayload()
    {
        TestBlockHeaderJson genesis = GenesisHeader(baseFeePerGas: "0x07");
        genesis.Difficulty = "0x00";
        genesis.Hash = HashOf(genesis);
        // Number 2 on genesis skips a block number, which header validation rejects.
        TestBlockHeaderJson child = ChildHeader(genesis, "0x02");
        string blockHash = HashOf(child);

        return ($$"""
            {
              "parentHash": "{{child.ParentHash}}", "feeRecipient": "{{child.Coinbase}}", "stateRoot": "{{child.StateRoot}}",
              "receiptsRoot": "{{child.ReceiptTrie}}", "logsBloom": "{{child.Bloom}}", "prevRandao": "{{child.MixHash}}",
              "blockNumber": "{{child.Number}}", "gasLimit": "{{child.GasLimit}}", "gasUsed": "{{child.GasUsed}}",
              "timestamp": "{{child.Timestamp}}", "extraData": "{{child.ExtraData}}", "baseFeePerGas": "{{child.BaseFeePerGas}}",
              "blockHash": "{{blockHash}}", "transactions": []
            }
            """, blockHash);
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
        header.Hash = HashOf(header);
        return header;
    }

    private static string HashOf(TestBlockHeaderJson header) =>
        Rlp.Decode<Block>(Rlp.Encode(new Block(JsonToEthereumTest.Convert(header))).Bytes).Header.Hash!.ToString();
}
