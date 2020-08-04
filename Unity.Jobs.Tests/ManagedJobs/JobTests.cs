using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;

namespace Unity.Jobs.Tests.ManagedJobs
{
	[JobProducerType(typeof(IJobTestExtensions.JobTestProducer<>))]
	public interface IJobTest
	{
		void Execute();
	}

	public static class IJobTestExtensions
	{
        internal struct JobTestWrapper<T> where T : struct
        {
            internal T JobData;

            [NativeDisableContainerSafetyRestriction]
            [DeallocateOnJobCompletion]
            internal NativeArray<byte> ProducerResourceToClean;
        }

		internal struct JobTestProducer<T> where T : struct, IJobTest
		{
			static IntPtr s_JobReflectionData;

			public static IntPtr Initialize()
			{
				if (s_JobReflectionData == IntPtr.Zero)
				{
#if UNITY_2020_2_OR_NEWER
					s_JobReflectionData = JobsUtility.CreateJobReflectionData(typeof(JobTestWrapper<T>), typeof(T), (ExecuteJobFunction)Execute);
#else
					s_JobReflectionData = JobsUtility.CreateJobReflectionData(typeof(JobTestWrapper<T>), typeof(T),
						JobType.Single, (ExecuteJobFunction)Execute);
#endif
				}

				return s_JobReflectionData;
			}

			public delegate void ExecuteJobFunction(ref JobTestWrapper<T> jobWrapper, IntPtr additionalPtr, IntPtr bufferRangePatchData, ref JobRanges ranges, int jobIndex);
			public unsafe static void Execute(ref JobTestWrapper<T> jobWrapper, IntPtr additionalPtr, IntPtr bufferRangePatchData, ref JobRanges ranges, int jobIndex)
			{
				jobWrapper.JobData.Execute();
			}
		}

		public static unsafe JobHandle ScheduleTest<T>(this T jobData, NativeArray<byte> dataForProducer, JobHandle dependsOn = new JobHandle()) where T : struct, IJobTest
		{
			JobTestWrapper<T> jobTestWrapper = new JobTestWrapper<T>
			{
				JobData = jobData,
				ProducerResourceToClean = dataForProducer
			};
			
			var scheduleParams = new JobsUtility.JobScheduleParameters(
				UnsafeUtility.AddressOf(ref jobTestWrapper),
				JobTestProducer<T>.Initialize(),
				dependsOn,
#if UNITY_2020_2_OR_NEWER
				ScheduleMode.Parallel
#else
				ScheduleMode.Batched
#endif
			);

			return JobsUtility.Schedule(ref scheduleParams);
		}
	}

    public class JobTests : JobTestsFixture
    {
        /*
         * these two tests used to test that a job that inherited from both IJob and IJobParallelFor would work as expected
         * but that's probably crazy.
         */
        /*[Test]
        public void Scheduling()
        {
            var job = data.Schedule();
            job.Complete();
            ExpectOutputSumOfInput0And1();
        }*/


        /*[Test]

        public void Scheduling_With_Dependencies()
        {
            data.input0 = input0;
            data.input1 = input1;
            data.output = output2;
            var job1 = data.Schedule();

            // Schedule job2 with dependency against the first job
            data.input0 = output2;
            data.input1 = input2;
            data.output = output;
            var job2 = data.Schedule(job1);

            // Wait for completion
            job2.Complete();
            ExpectOutputSumOfInput0And1And2();
        }*/

        [Test]
        public void ForEach_Scheduling_With_Dependencies()
        {
            data.input0 = input0;
            data.input1 = input1;
            data.output = output2;
            var job1 = data.Schedule(output.Length, 1);

            // Schedule job2 with dependency against the first job
            data.input0 = output2;
            data.input1 = input2;
            data.output = output;
            var job2 = data.Schedule(output.Length, 1, job1);

            // Wait for completion
            job2.Complete();
            ExpectOutputSumOfInput0And1And2();
        }

        struct EmptyComputeParallelForJob : IJobParallelFor
        {
            public void Execute(int i)
            {
            }
        }

        [Test]
        public void ForEach_Scheduling_With_Zero_Size()
        {
            var test = new EmptyComputeParallelForJob();
            var job = test.Schedule(0, 1);
            job.Complete();
        }

        [Test]
        public void Deallocate_Temp_NativeArray_From_Job()
        {
            TestDeallocateNativeArrayFromJob(Allocator.TempJob);
        }

        [Test]
        public void Deallocate_Persistent_NativeArray_From_Job()
        {
            TestDeallocateNativeArrayFromJob(Allocator.Persistent);
        }

        private void TestDeallocateNativeArrayFromJob(Allocator label)
        {
            var tempNativeArray = new NativeArray<int>(expectedInput0, label);

            var copyAndDestroyJob = new CopyAndDestroyNativeArrayParallelForJob
            {
                input = tempNativeArray,
                output = output
            };

            // NativeArray can safely be accessed before scheduling
            Assert.AreEqual(10, tempNativeArray.Length);

            tempNativeArray[0] = tempNativeArray[0];

            var job = copyAndDestroyJob.Schedule(copyAndDestroyJob.input.Length, 1);

            job.Complete();

            Assert.AreEqual(expectedInput0, copyAndDestroyJob.output.ToArray());
        }

        public struct NestedDeallocateStruct
        {
			// This should deallocate even though it's a nested field
            [DeallocateOnJobCompletion]
            public NativeArray<int> input;
        }

        public struct TestNestedDeallocate : IJob
        {
            public NestedDeallocateStruct nested;

            public NativeArray<int> output;

            public void Execute()
            {
                for (int i = 0; i < nested.input.Length; ++i)
                    output[i] = nested.input[i];
            }
        }

        [Test]
        public void TestNestedDeallocateOnJobCompletion()
        {
            var tempNativeArray = new NativeArray<int>(10, Allocator.TempJob);
            var outNativeArray = new NativeArray<int>(10, Allocator.TempJob);
            for (int i = 0; i < 10; i++)
                tempNativeArray[i] = i;

            var job = new TestNestedDeallocate
            {
                nested = new NestedDeallocateStruct() { input = tempNativeArray },
                output = outNativeArray
            };

            var handle = job.Schedule();
            handle.Complete();

            outNativeArray.Dispose();

			// Ensure released safety handle indicating invalid buffer
			Assert.Throws<InvalidOperationException>(() => { AtomicSafetyHandle.CheckExistsAndThrow(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(tempNativeArray)); });
			Assert.Throws<InvalidOperationException>(() => { AtomicSafetyHandle.CheckExistsAndThrow(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(job.nested.input)); });
        }


        public struct TestJobProducerJob : IJobTest
        {
			[DeallocateOnJobCompletion]
            public NativeArray<int> jobStructData;

            public void Execute()
            {
            }
        }

        [Test]
        public void TestJobProducerCleansUp()
        {
            var tempNativeArray = new NativeArray<int>(10, Allocator.TempJob);
            var tempNativeArray2 = new NativeArray<byte>(16, Allocator.TempJob);

            var job = new TestJobProducerJob
            {
                jobStructData = tempNativeArray,
            };

            var handle = job.ScheduleTest(tempNativeArray2);
            handle.Complete();

			// Check job data
			Assert.Throws<InvalidOperationException>(() => { AtomicSafetyHandle.CheckExistsAndThrow(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(tempNativeArray)); });
			Assert.Throws<InvalidOperationException>(() => { AtomicSafetyHandle.CheckExistsAndThrow(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(job.jobStructData)); });
			// Check job producer
			Assert.Throws<InvalidOperationException>(() => { AtomicSafetyHandle.CheckExistsAndThrow(NativeArrayUnsafeUtility.GetAtomicSafetyHandle(tempNativeArray2)); });
        }
    }
}
