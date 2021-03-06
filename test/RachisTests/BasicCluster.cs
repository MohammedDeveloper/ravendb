﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Raven.Server.Rachis;
using Raven.Server.ServerWide.Context;
using Sparrow.Json;
using Tests.Infrastructure;
using Xunit;

namespace RachisTests
{
    public class BasicCluster : RachisConsensusTestBase
    {
        [NightlyBuildFact]
        public async Task ClusterWithFiveNodesAndMultipleElections()
        {
            PredictableSeeds = true;
            var a = SetupServer(true);
            var b = SetupServer();
            var c = SetupServer();
            var d = SetupServer();
            var e = SetupServer();

            var followers = new[] { b, c, d, e };

            foreach (var follower in followers)
            {
                await a.AddToClusterAsync(follower.Url);
                await follower.WaitForTopology(Leader.TopologyModification.Voter);
            }

            var leaderSelected = followers.Select(x => x.WaitForState(RachisConsensus.State.Leader).ContinueWith(_ => x)).ToArray();

            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (int i = 0; i < 10; i++)
                {
                    await a.PutAsync(new TestCommand { Name = "test", Value = i });
                }
            }


            foreach (var follower in followers)
            {
                Disconnect(follower.Url, a.Url);
            }

            var leader = await await Task.WhenAny(leaderSelected);



            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (int i = 10; i < 20; i++)
                {
                    await leader.PutAsync(new TestCommand { Name = "test", Value = i });
                }
            }

            followers = followers.Except(new[] { leader }).ToArray();

            leaderSelected = followers.Select(x => x.WaitForState(RachisConsensus.State.Leader).ContinueWith(_ => x)).ToArray();

            foreach (var follower in followers)
            {
                Disconnect(follower.Url, leader.Url);
            }

            leader = await await Task.WhenAny(leaderSelected);



            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (int i = 20; i < 30; i++)
                {
                    await leader.PutAsync(new TestCommand { Name = "test", Value = i });
                }
            }

            TransactionOperationContext context;
            using (leader.ContextPool.AllocateOperationContext(out context))
            using (context.OpenReadTransaction())
            {
                var actual = leader.StateMachine.Read(context, "test");
                var expected = Enumerable.Range(0, 30).Sum();
                Assert.Equal(expected, actual);
            }
        }

        [NightlyBuildFact]
        public async Task ClusterWithThreeNodesAndElections()
        {
            var a = SetupServer(true);
            var b = SetupServer();
            var c = SetupServer();

            var bUpgraded = b.WaitForTopology(Leader.TopologyModification.Voter);
            var cUpgraded = c.WaitForTopology(Leader.TopologyModification.Voter);

            await a.AddToClusterAsync(b.Url);
            await b.WaitForTopology(Leader.TopologyModification.Voter);
            await a.AddToClusterAsync(c.Url);
            await c.WaitForTopology(Leader.TopologyModification.Voter);

            await bUpgraded;
            await cUpgraded;

            var bLeader = b.WaitForState(RachisConsensus.State.Leader);
            var cLeader = c.WaitForState(RachisConsensus.State.Leader);

            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (int i = 0; i < 10; i++)
                {
                    await a.PutAsync(new TestCommand { Name = "test", Value = i });
                }
            }

            Disconnect(b.Url, a.Url);
            Disconnect(c.Url, a.Url);


            await Task.WhenAny(bLeader, cLeader);
        }

        [NightlyBuildFact]
        public async Task ClusterWithLateJoiningNodeRequiringSnapshot()
        {
            var expected = 45;
            var a = SetupServer(true);

            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (var i = 0; i < 5; i++)
                {
                    await a.PutAsync(new TestCommand { Name = "test", Value = i });
                }
            }

            var b = SetupServer();

            await a.AddToClusterAsync(b.Url);
            await b.WaitForTopology(Leader.TopologyModification.Voter);
            long lastIndex = 0;
            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (var i = 0; i < 5; i++)
                {
                    var (etag, _) = await a.PutAsync(new TestCommand { Name = "test", Value = i + 5 });
                    lastIndex = etag;
                }
            }

            Assert.True(await b.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, lastIndex).WaitAsync(15000));
            TransactionOperationContext context;
            using (b.ContextPool.AllocateOperationContext(out context))
            using (context.OpenReadTransaction())
            {
                Assert.Equal(expected, b.StateMachine.Read(context, "test"));
            }
        }

        [NightlyBuildFact]
        public async Task ClusterWithTwoNodes()
        {
            var expected = 45;
            var a = SetupServer(true);
            var b = SetupServer();

            await a.AddToClusterAsync(b.Url);
            await b.WaitForTopology(Leader.TopologyModification.Voter);

            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                var tasks = new List<Task>();
                for (var i = 0; i < 9; i++)
                {
                    tasks.Add(a.PutAsync(new TestCommand { Name = "test", Value = i }));
                }

                var (lastIndex, _) = await a.PutAsync(new TestCommand { Name = "test", Value = 9 });

                foreach (var task in tasks)
                {
                    Assert.Equal(TaskStatus.RanToCompletion, task.Status);
                }

                var waitForCommitIndexChange = b.WaitForCommitIndexChange(RachisConsensus.CommitIndexModification.GreaterOrEqual, lastIndex);

                Assert.True(await waitForCommitIndexChange.WaitAsync(TimeSpan.FromSeconds(5)));

                TransactionOperationContext context;
                using (b.ContextPool.AllocateOperationContext(out context))
                using (context.OpenReadTransaction())
                {
                    Assert.Equal(expected, b.StateMachine.Read(context, "test"));
                }
            }
        }

        [NightlyBuildFact]
        public async Task CanSetupSingleNode()
        {
            var rachis = SetupServer(true);

            using (var ctx = JsonOperationContext.ShortTermSingleUse())
            {
                for (var i = 0; i < 10; i++)
                {
                    await rachis.PutAsync(new TestCommand { Name = "test", Value = i });
                }

                TransactionOperationContext context;
                using (rachis.ContextPool.AllocateOperationContext(out context))
                using (context.OpenReadTransaction())
                {
                    Assert.Equal(45, rachis.StateMachine.Read(context, "test"));
                }
            }
        }
    }
}