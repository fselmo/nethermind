// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class BlockchainTestConversionTests
{
    [Test]
    public void ConvertToBlockchainTests_PopulatesForkName([Values("Amsterdam", "ParisToShanghaiAtTime15k")] string network)
    {
        string json = $$"""
            {
              "tests/some/test.py::test_case[fork_{{network}}-blockchain_test]": {
                "network": "{{network}}",
                "lastblockhash": "0x281f01f5b9b9a5237ec39ac315e2e3a01017c37a83f6f0e26689b29e421a0311",
                "pre": {}
              }
            }
            """;

        List<BlockchainTest> tests = [.. JsonToEthereumTest.ConvertToBlockchainTests(json)];

        Assert.That(tests, Has.Count.EqualTo(1));
        Assert.That(tests.Single().ForkName, Is.EqualTo(network));
    }

    [TestCase("blockAccessList", TestName = "Access list of a valid block")]
    [TestCase("rlp_decoded", TestName = "Access list of an expected-invalid block")]
    public void DeliveredBlockAccessList_is_read_from_where_the_fixture_puts_it(string location)
    {
        const string accessList = """
            [{
              "address": "0x000f3df6d732807ef1319fb7b8bb8522d0beac02",
              "nonceChanges": [{ "blockAccessIndex": "0x01", "postNonce": "0x01" }],
              "balanceChanges": [{ "blockAccessIndex": "0x01", "postBalance": "0x0a" }],
              "codeChanges": [],
              "storageChanges": [{ "slot": "0x01", "slotChanges": [{ "blockAccessIndex": "0x01", "postValue": "0x02" }] }],
              "storageReads": ["0x03"]
            }]
            """;
        string block = location == "rlp_decoded"
            ? $$"""{ "rlp": "0x", "expectException": "BlockException.INVALID_BLOCK_ACCESS_LIST", "rlp_decoded": { "blockAccessList": {{accessList}} } }"""
            : $$"""{ "rlp": "0x", "blockAccessList": {{accessList}} }""";

        TestBlockJson testBlock = new EthereumJsonSerializer().Deserialize<TestBlockJson>(block);
        ReadOnlyBlockAccessList delivered = BlockAccessListAccountJson.ToBlockAccessList(testBlock.DeliveredBlockAccessList!);

        ReadOnlyAccountChanges account = delivered.AccountChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Address, Is.EqualTo(new Address("0x000f3df6d732807ef1319fb7b8bb8522d0beac02")));
            Assert.That(account.NonceChanges, Is.EqualTo(new[] { new NonceChange(1, 1) }));
            Assert.That(account.BalanceChanges, Is.EqualTo(new[] { new BalanceChange(1, 10) }));
            Assert.That(account.StorageChanges.Single().Changes, Is.EqualTo(new[] { new StorageChange(1, 2) }));
            Assert.That(account.StorageReads, Is.EqualTo(new[] { (UInt256)3 }));
        }
    }
}
