﻿using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using System.Diagnostics;
using System.Collections.Generic;

namespace CosmosGlobalDistDemos
{

    /*
        * Resources needed for this demo:
        * 
        * Shared for all demos in this solution
        * - Windows VM, West US 2, Standard B4 (4 core, 16GB), RDP enabled. This solution gets run from the VM.
        * 
        *   Single Region => Cosmos DB account: Replication: Single-Master, Write Region: Southeast Asia, Consistency: Eventual
        *   Multi-Region => Cosmos DB account: Replication: Single-Master, Write Region: Southeast Asia, Read Region: West US 2, Consistency: Eventual
        *   
    */



    class SingleMultiRegion
    {
        private string databaseName;
        private string containerName;
        private Uri databaseUri;
        private Uri containerUri;
        private string PartitionKeyProperty = ConfigurationManager.AppSettings["PartitionKeyProperty"];
        private string PartitionKeyValue = ConfigurationManager.AppSettings["PartitionKeyValue"];
        private DocumentClient clientSingle;
        private DocumentClient clientMulti;

        private Bogus.Faker<SampleCustomer> customerGenerator = new Bogus.Faker<SampleCustomer>().Rules((faker, customer) =>
            {
                customer.Id = Guid.NewGuid().ToString();
                customer.Name = faker.Name.FullName();
                customer.City = faker.Person.Address.City.ToString();
                customer.Region = faker.Person.Address.State.ToString();
                customer.PostalCode = faker.Person.Address.ZipCode.ToString();
                customer.MyPartitionKey = ConfigurationManager.AppSettings["PartitionKeyValue"];
                customer.UserDefinedId = faker.Random.Int(0, 1000);
            }
        );

        private List<ResultData> results;

        public SingleMultiRegion()
        {
            Console.WriteLine("Single Region vs Multi-Region Read Latency");
            Console.WriteLine("-------------------------------------------------------------------------");

            string endpoint, key, region;
            
            databaseName = ConfigurationManager.AppSettings["database"];
            containerName = ConfigurationManager.AppSettings["container"];
            databaseUri = UriFactory.CreateDatabaseUri(databaseName);
            containerUri = UriFactory.CreateDocumentCollectionUri(databaseName, containerName);

            //Single-Region account client
            endpoint = ConfigurationManager.AppSettings["SingleRegionEndpoint"];
            key = ConfigurationManager.AppSettings["SingleRegionKey"];
            region = ConfigurationManager.AppSettings["SingleRegionRegion"];

            ConnectionPolicy policy = new ConnectionPolicy
            {
                ConnectionMode = ConnectionMode.Direct,
                ConnectionProtocol = Protocol.Tcp,
            };

            policy.SetCurrentLocation(region);
            clientSingle = new DocumentClient(new Uri(endpoint), key, policy, ConsistencyLevel.Eventual);
            clientSingle.OpenAsync();

            Console.WriteLine($"Created DocumentClient for Single-Region account in: {region}.");

            //Multi-Region account client
            endpoint = ConfigurationManager.AppSettings["MultiRegionEndpoint"];
            key = ConfigurationManager.AppSettings["MultiRegionKey"];
            region = ConfigurationManager.AppSettings["MultiRegionRegion"];

            policy.SetCurrentLocation(region);
            clientMulti = new DocumentClient(new Uri(endpoint), key, policy, ConsistencyLevel.Eventual);
            clientMulti.OpenAsync();

            Console.WriteLine($"Created DocumentClient for Multi-Region account in: {region}.");
            Console.WriteLine();
        }

        public async Task RunDemo()
        {
            results = new List<ResultData>();

            Console.WriteLine("Test Read Latency between a Single Region Account vs Multi-Region Account");
            Console.WriteLine("-------------------------------------------------------------------------");
            Console.WriteLine();

            await ReadBenchmark(clientSingle, "Single-Region");
            await ReadBenchmark(clientMulti, "Multi-Region", true);
        }

        public async Task Initalize()
        {
            Console.WriteLine("Single/Multi Region Initialize");
            //Database definition
            Database database = new Database { Id = databaseName };

            //Container definition
            RequestOptions options = new RequestOptions { OfferThroughput = 1000 };
            PartitionKeyDefinition partitionKeyDefinition = new PartitionKeyDefinition();
            partitionKeyDefinition.Paths.Add(PartitionKeyProperty);
            DocumentCollection container = new DocumentCollection { Id = containerName, PartitionKey = partitionKeyDefinition };


            //Single Region
            //create the database
            await clientSingle.CreateDatabaseIfNotExistsAsync(database);
            //Create the container
            await clientSingle.CreateDocumentCollectionIfNotExistsAsync(databaseUri, container, options);


            //Multi Region
            //create the database
            await clientMulti.CreateDatabaseIfNotExistsAsync(database);
            //Create the container
            await clientMulti.CreateDocumentCollectionIfNotExistsAsync(databaseUri, container, options);

            await Populate();
        }

        private async Task Populate()
        {
            var sql = "SELECT * FROM c";

            FeedOptions feedOptions = new FeedOptions
            {
                PartitionKey = new PartitionKey(PartitionKeyValue)
            };

            string region = ParseEndpoint(clientSingle.WriteEndpoint);

            var documents = clientSingle.CreateDocumentQuery(containerUri, sql, feedOptions).ToList();
            if (documents.Count == 0)
            {
                Console.WriteLine($"Populating Single Region container");
                for (int i = 0; i < 100; i++)
                {
                    SampleCustomer customer = customerGenerator.Generate();
                    await clientSingle.CreateDocumentAsync(containerUri, customer);
                }
                Console.WriteLine($"Populating complete");
            }
            

            region = ParseEndpoint(clientMulti.WriteEndpoint);

            documents = clientMulti.CreateDocumentQuery(containerUri, sql, feedOptions).ToList();
            if (documents.Count == 0)
            {
                Console.WriteLine($"Populating Multi Region container");
                for (int i = 0; i < 100; i++)
                {
                    SampleCustomer customer = customerGenerator.Generate();
                    await clientMulti.CreateDocumentAsync(containerUri, customer);
                }
                Console.WriteLine($"Populating complete");
            }
        }

        private async Task ReadBenchmark(DocumentClient client, string replicaType, bool final = false)
        {
            string region = ParseEndpoint(client.ReadEndpoint);

            FeedOptions feedOptions = new FeedOptions
            {
                PartitionKey = new PartitionKey(PartitionKeyValue)
            };
            string sql = "SELECT * FROM c";
            var items = client.CreateDocumentQuery(containerUri, sql, feedOptions).ToList();

            int i = 0;
            int total = items.Count;
            int lt = 0;
            double ru = 0;

            Console.WriteLine();
            Console.WriteLine($"Test {total} reads against {replicaType} account in {region}\r\nPress any key to continue\r\n...");
            Console.ReadKey(true);

            RequestOptions requestOptions = new RequestOptions
            {
                PartitionKey = new PartitionKey(PartitionKeyValue)
            };

            foreach (Document item in items)
            {
                ResourceResponse<Document> response = await client.ReadDocumentAsync(item.SelfLink, requestOptions);
                Console.WriteLine($"Read {i} of {total}, region: {region}, Latency: {response.RequestLatency.Milliseconds} ms, Request Charge: {response.RequestCharge} RUs");
                lt += response.RequestLatency.Milliseconds;
                ru += response.RequestCharge;
                i++;
            }
            results.Add(new ResultData
            {
                Test = $"Test reads against {replicaType} account in {region}",
                AvgLatency = (lt / total).ToString(),
                AvgRU = Math.Round(ru / total).ToString()
            });
            Console.WriteLine();
            Console.WriteLine("Summary");
            Console.WriteLine("-----------------------------------------------------------------------------------------------------");
            Console.WriteLine($"Test {total} reads against {replicaType} account in {region}");
            Console.WriteLine();
            Console.WriteLine($"Average Latency:\t{(lt / total)} ms");
            Console.WriteLine($"Average Request Units:\t{Math.Round(ru / total)} RUs");
            Console.WriteLine();
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey(true);

            if (final)
            {
                Console.WriteLine();
                Console.WriteLine("Summary");
                Console.WriteLine("-----------------------------------------------------------------------------------------------------");
                foreach (ResultData r in results)
                {
                    Console.WriteLine($"{r.Test}\tAvg Latency: {r.AvgLatency} ms\tAverage RU: {r.AvgRU}");
                }
                Console.WriteLine();
                Console.WriteLine($"Test concluded. Press any key to continue\r\n...");
                Console.ReadKey(true);
            }
        }

        public async Task CleanUp()
        {
            try
            {
                await clientSingle.DeleteDatabaseAsync(databaseUri);
                await clientMulti.DeleteDatabaseAsync(databaseUri);
            }
            catch { }
        }

        public string ParseEndpoint(Uri endPoint)
        {
            string x = endPoint.ToString();

            int tail = x.IndexOf(".documents.azure.com");
            int head = x.LastIndexOf("-") + 1;

            return x.Substring(head, (tail - head));
        }
    }
}