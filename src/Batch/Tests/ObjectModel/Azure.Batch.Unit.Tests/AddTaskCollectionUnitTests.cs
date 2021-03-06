﻿namespace Azure.Batch.Unit.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using BatchTestCommon;
    using Microsoft.Azure.Batch;
    using Microsoft.Azure.Batch.Protocol.Models;
    using Microsoft.Rest.Azure;
    using Xunit;
    using Xunit.Abstractions;
    using CloudTask = Microsoft.Azure.Batch.CloudTask;
    using Protocol = Microsoft.Azure.Batch.Protocol;
    
    public class AddTaskCollectionUnitTests
    {
        private readonly ITestOutputHelper testOutputHelper;

        public AddTaskCollectionUnitTests(ITestOutputHelper testOutputHelper)
        {
            this.testOutputHelper = testOutputHelper;
        }

        [Fact]
        [Trait(TestTraits.Duration.TraitName, TestTraits.Duration.Values.VeryShortDuration)]
        public async Task AddTaskCollectionNoHandlerThrows()
        {
            const string dummyJobId = "Dummy";
            using (BatchClient batchCli = BatchClient.Open(ClientUnitTestCommon.CreateDummySharedKeyCredential()))
            {
                //Clear the behaviors so that there is no way there is a AddTaskResultHandler defined
                batchCli.CustomBehaviors.Clear();

                CloudTask task = new CloudTask("Foo", "Bar");
                
                BatchClientException exception = await Assert.ThrowsAsync<BatchClientException>(() => batchCli.JobOperations.AddTaskAsync(dummyJobId, new List<CloudTask> { task }));
                string expectedString = string.Format(BatchErrorMessages.GeneralBehaviorMissing, typeof (AddTaskCollectionResultHandler));
                Assert.Equal(expectedString, exception.Message);
            }
        }

        [Fact]
        [Trait(TestTraits.Duration.TraitName, TestTraits.Duration.Values.VeryShortDuration)]
        public async Task AddTaskCollectionNullTaskThrows()
        {
            const string dummyJobId = "Dummy";
            using (BatchClient batchCli = BatchClient.Open(ClientUnitTestCommon.CreateDummySharedKeyCredential()))
            {
                ArgumentNullException exception = await Assert.ThrowsAsync<ArgumentNullException>(() => batchCli.JobOperations.AddTaskAsync(dummyJobId, new List<CloudTask> { null }));
                string expectedString = string.Format(BatchErrorMessages.CollectionMustNotContainNull);
                Assert.Contains(expectedString, exception.Message);
            }
        }

        [Theory, InlineData(1), InlineData(2)]
        [Trait(TestTraits.Duration.TraitName, TestTraits.Duration.Values.VeryShortDuration)]
        public async Task ExceptionOnRestOperationIsWrappedInParallelOperationsException(int operationCount)
        {
            ParallelOperationsException exception = await IssueAddTaskCollectionAndAssertExceptionIsExpectedAsync<ParallelOperationsException>(
                operationCount, 
                (count, message) => new ArgumentException(message));

            Assert.Contains("One or more requests to the Azure Batch service failed", exception.ToString());
            Assert.Equal(operationCount, exception.InnerExceptions.Count);
            foreach (Exception innerException in exception.InnerExceptions)
            {
                Assert.IsType<ArgumentException>(innerException);
            }
        }
        
        [Fact]
        [Trait(TestTraits.Duration.TraitName, TestTraits.Duration.Values.VeryShortDuration)]
        //NOTE: This is the current behavior but not sure if it makes sense... but fixing it is quite difficult/tricky...
        public async Task OperationCanceledExceptionRestOperations_OnlyOneIsSeen()
        {
            const int operationCount = 2;
            await IssueAddTaskCollectionAndAssertExceptionIsExpectedAsync<OperationCanceledException>(
                operationCount,
                (count, message) => new OperationCanceledException(message));
        }

        [Fact]
        [Trait(TestTraits.Duration.TraitName, TestTraits.Duration.Values.VeryShortDuration)]
        public async Task ExceptionOnSomeRestOperationsAllWrappedInParallelOperationsException()
        {
            const int operationCount = 4;
            const int modFactor = 2;
            ParallelOperationsException exception = await IssueAddTaskCollectionAndAssertExceptionIsExpectedAsync<ParallelOperationsException>(
                operationCount,
                (count, message) =>
                    {
                        if (count % modFactor == 0)
                        {
                            return new ArgumentException(message);
                        }
                        else
                        {
                            return null;
                        }
                    });

            Assert.Contains("One or more requests to the Azure Batch service failed", exception.ToString());
            Assert.Equal(operationCount / modFactor, exception.InnerExceptions.Count);
            foreach (Exception innerException in exception.InnerExceptions)
            {
                Assert.IsType<ArgumentException>(innerException);
            }
        }

        private async static Task<T> IssueAddTaskCollectionAndAssertExceptionIsExpectedAsync<T>(int operationCount, Func<int, string, Exception> exceptionFactory) where T : Exception
        {
            const string dummyJobId = "Dummy";
            int taskCountToAdd = operationCount * Constants.MaxTasksInSingleAddTaskCollectionRequest;

            using (BatchClient batchCli = BatchClient.Open(ClientUnitTestCommon.CreateDummySharedKeyCredential()))
            {
                AddExceptionGeneratingBehavior(batchCli, exceptionFactory);

                IEnumerable<CloudTask> cloudTaskList = CreateCloudTasks(taskCountToAdd);

                T exception = await Assert.ThrowsAsync<T>(() => batchCli.JobOperations.AddTaskAsync(
                    dummyJobId,
                    cloudTaskList,
                    parallelOptions: new BatchClientParallelOptions()
                        {
                            MaxDegreeOfParallelism = operationCount
                        }));

                return exception;
            }
        }

        private static IEnumerable<CloudTask> CreateCloudTasks(int count)
        {
            var cloudTaskList = new List<CloudTask>();
            for (int i = 0; i < count; i++)
            {
                cloudTaskList.Add(new CloudTask("i" + i, "cmd /c dir"));
            }

            return cloudTaskList;
        }

        private static void AddExceptionGeneratingBehavior(BatchClient batchCli, Func<int, string, Exception> exceptionGenerator)
        {
            int count = 0;
            object lockObject = new object();
            const string exceptionString = "count is: {0}";
            batchCli.CustomBehaviors.Add(new Protocol.RequestInterceptor(req =>
            {
                var addTaskCollectionRequest = req as Protocol.BatchRequests.TaskAddCollectionBatchRequest;
                addTaskCollectionRequest.ServiceRequestFunc = ct =>
                {
                    lock (lockObject)
                    {
                        count++;
                        Exception e = exceptionGenerator(count, string.Format(exceptionString, count));
                        if (e != null)
                        {
                            throw e;
                        }
                        else
                        {
                            return Task.FromResult(new AzureOperationResponse<Protocol.Models.TaskAddCollectionResult, Protocol.Models.TaskAddCollectionHeaders>()
                                {
                                    Body = new Protocol.Models.TaskAddCollectionResult(new List<TaskAddResult>())
                                });
                        }
                    }
                };
            }));
        }
    }
}
