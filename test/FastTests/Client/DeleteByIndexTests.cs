using System;
using System.Linq;
using System.Threading.Tasks;
using Raven.Client;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Session;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Json;
using Xunit;

namespace FastTests.Client
{
    public class IndexesDeleteByIndexTests : RavenNewTestBase
    {
        [Fact]
        public void Delete_By_Index()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User { Name = "Fitzchak" });
                    session.Store(new User { Name = "Arek" });
                    session.SaveChanges();
                }

                string indexName;
                using (var session = store.OpenSession())
                {
                    QueryStatistics stats;
                    var people = session.Query<User>()
                        .Customize(x => x.WaitForNonStaleResults())
                        .Statistics(out stats)
                        .Where(x => x.Name == "Arek")
                        .ToList();

                    indexName = stats.IndexName;
                }

                JsonOperationContext context;
                store.GetRequestExecuter(store.DefaultDatabase).ContextPool.AllocateOperationContext(out context);

                var operation = store.Operations.Send(new DeleteByIndexOperation(indexName, new IndexQuery(store.Conventions), new QueryOperationOptions { AllowStale = false }));

                operation.WaitForCompletion(TimeSpan.FromSeconds(60));

                var databaseStatistics = store.Admin.Send(new GetStatisticsOperation());

                Assert.Equal(1, databaseStatistics.CountOfDocuments);
            }
        }

        [Fact]
        public async Task Delete_By_Index_Async()
        {
            using (var store = GetDocumentStore())
            {
                using (var session = store.OpenAsyncSession())
                {
                    await session.StoreAsync(new User { Name = "Fitzchak" }, "users/1");
                    await session.StoreAsync(new User { Name = "Arek" }, "users/2");
                    await session.SaveChangesAsync();
                }

                string indexName;
                using (var session = store.OpenSession())
                {
                    QueryStatistics stats;
                    var people = session.Query<User>()
                        .Customize(x => x.WaitForNonStaleResults())
                        .Statistics(out stats)
                        .Where(x => x.Name == "Arek")
                        .ToList();

                    indexName = stats.IndexName;
                }

                JsonOperationContext context;
                store.GetRequestExecuter(store.DefaultDatabase).ContextPool.AllocateOperationContext(out context);

                var operation = await store.Operations.SendAsync(new DeleteByIndexOperation(indexName, new IndexQuery(store.Conventions), new QueryOperationOptions { AllowStale = false }));

                await operation.WaitForCompletionAsync(TimeSpan.FromSeconds(60));

                var databaseStatistics = store.Admin.Send(new GetStatisticsOperation());

                //TODO - after we have hilo need to be 1
                Assert.Equal(0, databaseStatistics.CountOfDocuments);
            }
        }
    }
}