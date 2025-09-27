

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Synqra.Tests.TestHelpers;

public class PerformanceResults
{
	public double OperationsPerSecond { get; set; }
	public double DeviationFactor { get; set; }
}

public class PerformanceParameters
{
	/// <summary>
	/// Initial Batch Size.
	/// A batch should be big enough to avoid overhead of the loop, but small enough to fit deviation measurement in destination time frame.
	/// Batch size will grow exponentially to reasonable value, so 1 is totally fine here.
	/// Final measurement will be done with the calculated auto-grown batch size.
	/// </summary>
	// public int StartingBatchSize { get; set; } = 1;

	/// <summary>
	/// How many batch execution is required to calculate deviation factor.
	/// </summary>
	public int DeviationMeasurementBatches { get; set; } = 5; // we throw away 2 outsiders

	/// <summary>
	/// This is a time that we allow test to take after batch size determined.
	/// This includes total duration of all batches but does not include auto-batching time. Due to exponential nature it should not take longer than %50 of the total time.
	/// </summary>
	public TimeSpan? TotalTargetTime { get; set; }

	/// <summary>
	/// This is a time that we want for one batch.
	/// </summary>
	public TimeSpan? BatchTime { get; set; } = TimeSpan.FromMilliseconds(300);

	/// <summary>
	/// When deviation factor is calculated, it is not necessary to consume it. Instead, we can set the acceptable boundary here.
	/// If the action is too flaky, it will throw an exception.
	/// Default is 10%. Make sure the test is not parallel to avoid flaky results.
	/// </summary>
	public double MaxAcceptableDeviationFactor { get; set; } = 0.10;

	/// <summary>
	/// Diagnostic Logging with System.Diagnostics.Trace
	/// </summary>
	public bool Trace { get; set; } = true;
}


public class PerformanceTestUtils
{

	/*
	protected async Task<double> Measure(Func<Task> action, int batch = 1000)
	{
		var sw = Stopwatch.StartNew();
		sw.Start();
		int count = 0;
		while (sw.ElapsedMilliseconds < 1000)
		{
			for (int i = 0; i < batch; i++)
			{
				await action();
			}
			count += batch;
		}
		sw.Stop();
		return count / sw.Elapsed.TotalSeconds;
	}
	*/

	/// <summary>
	/// Measure performance in operations per second
	/// </summary>
	public static double MeasureOps(Action action, PerformanceParameters? parameters = default)
	{
		return MeasurePerformance(action, parameters).OperationsPerSecond;
	}


	[DllImport("kernel32.dll")]
	static extern IntPtr GetCurrentThread();

	[DllImport("kernel32.dll")]
	static extern IntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

	public static void SetThreadAffinity(uint mask)
	{
		IntPtr threadHandle = GetCurrentThread();
		SetThreadAffinityMask(threadHandle, new UIntPtr(mask));
	}

	/// <summary>
	/// Measurement happens automatically in 3 stages:
	/// 1. BatchSizing. To minmize overhead we need a reasonable batch size for main loop. The size is calculated automatically with exponential+calculated grows.
	/// 2. JIT Optimization & Caches. Now every time batch performs better than before - it is ignored and moved on. JIT+user cache can make several rounds of optimizations, we are waiting till new result is worse than TOP 2.
	/// 3. Now make N etalon batch runs, remove 1 best and 1 worst, calculate deviation.
	/// </summary>
	public static PerformanceResults MeasurePerformance(Action action, PerformanceParameters? parameters = default)
	{
		// ThreadPool.GetMinThreads(out var iw, out var icp); ThreadPool.GetMaxThreads(out var aw, out var acp);
		// ThreadPool.SetMinThreads(0, 0); ThreadPool.SetMaxThreads(1, 1);
		// ThreadPool.GetMinThreads(out var iw2, out var icp2); ThreadPool.GetMaxThreads(out var aw2, out var acp2);
		// For i9 14900KS 8P+16E meansh 16HP+16E, limit to every even P to jump over Hyper and Efficiency cores
		// Process.GetCurrentProcess().ProcessorAffinity = 0b_0000_0101_0101_0101_0101; // to stabelize deviation measurements
		// SetThreadAffinity(0b_0000_0101_0101_0101_0101);

		var actualParameters = parameters ?? new PerformanceParameters();
		if (actualParameters.Trace)
		{
			Trace.WriteLine($"-----------MeasurePerformance-------------");
		}
		action(); // warm-up

		bool ignoreConsecutiveOptimizations = true;
		bool autoBatched = false;
		int batchSize = 1; // actualParameters.StartingBatchSize;
		int autoBatchBumps = 0;

	restart:
		if (actualParameters.Trace)
		{
			// Trace.WriteLine($"-------stage autoBatched={autoBatched} ignoreConsecutiveOptimizations={ignoreConsecutiveOptimizations} batchSize={batchSize}");
			Trace.WriteLine($"-------stage {(autoBatched, ignoreConsecutiveOptimizations) switch
			{
				(false, true) => "Batch Sizing",
				(true, true) => "Otimization & Caching",
				(true, false) => "Measure Deviation",
				_ => "??",
			}}...");
		}
		long operationsCount = 0;
		int batchIndex = 0;

		// Deviation Variables - are for per batch calculation after auto-batch finished
		TimeSpan deviationTotalDuration = default;
		TimeSpan deviationMin2OpsDuration = default;
		TimeSpan deviationMax2OpsDuration = default;
		// long deviationTotalCount = 0;
		int deviationTotalBatches = 0;
		double deviationMinOps = double.PositiveInfinity;
		double deviationMin2Ops = double.PositiveInfinity;
		double deviationMaxOps = 0;
		double deviationMax2Ops = 0;

		var sw = new Stopwatch();
#if NETSTANDARD
		var tst = actualParameters.TotalTargetTime?.Ticks / actualParameters.DeviationMeasurementBatches;
		var ts = tst == null ? default(TimeSpan?) : TimeSpan.FromTicks(tst.Value);
		var targetBatchDuration = actualParameters.BatchTime ?? ts ?? TimeSpan.FromMilliseconds(250);
#else
		var targetBatchDuration = actualParameters.BatchTime ?? (actualParameters.TotalTargetTime / actualParameters.DeviationMeasurementBatches) ?? TimeSpan.FromMilliseconds(250);
#endif
		while (deviationTotalBatches < actualParameters.DeviationMeasurementBatches || ignoreConsecutiveOptimizations == true)
		{
			// 
			if (actualParameters.Trace)
			{
				Trace.WriteLine($"[{sw.ElapsedMilliseconds,4}] batch #{batchIndex} batchSize={batchSize} targetBatchDuration={targetBatchDuration.TotalMilliseconds}ms");
			}
			var batchStart = sw.Elapsed;

			// We can't get reliable deviation if competing with GC too much
#if NETSTANDARD
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
#else
			GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
#endif
			GC.WaitForPendingFinalizers();
			// GC.TryStartNoGCRegion(int.MaxValue / 2, int.MaxValue / 2, true); // Allow 2GB (1GB SOH and 1GB LOH)
			Thread.Sleep(0); // Yield the remaining time slot back to scheuler and try to get fresh and full time slot.

			sw.Start();
			for (int i = 0; i < batchSize; i++)
			{
				action();
			}
			sw.Stop();
			checked
			{
				operationsCount += batchSize;
			}
			var batchEnd = sw.Elapsed;

			if (autoBatched)
			{
				var batchOps = batchSize / (batchEnd - batchStart).TotalSeconds;
				if (actualParameters.Trace)
				{
					Trace.WriteLine($"[{sw.ElapsedMilliseconds,4}] batch #{batchIndex} batchOps = {batchOps,12:N2} OpS {batchSize} in {(batchEnd - batchStart).TotalMilliseconds}ms");
				}

				if (batchOps < deviationMin2Ops && actualParameters.DeviationMeasurementBatches >= 5)
				{
					deviationMinOps = deviationMin2Ops;
					deviationMin2Ops = batchOps; // outsider
					deviationMin2OpsDuration = batchEnd - batchStart;
				}
				else if (batchOps < deviationMinOps)
				{
					deviationMinOps = batchOps; // 2nd min
				}

				if (batchOps > deviationMax2Ops && actualParameters.DeviationMeasurementBatches >= 5)
				{
					deviationMaxOps = deviationMax2Ops;
					deviationMax2Ops = batchOps; // outsider
					deviationMax2OpsDuration = batchEnd - batchStart;
				}
				else if (batchOps > deviationMaxOps || ignoreConsecutiveOptimizations)
				{
					deviationMaxOps = batchOps; // 2nd max
					if (ignoreConsecutiveOptimizations)
					{
						if (actualParameters.Trace)
						{
							Trace.WriteLine($"[{sw.ElapsedMilliseconds,4}] ignoreConsecutiveOptimizations done, reset");
						}
						ignoreConsecutiveOptimizations = false;

						// optimization stage done, reset
						goto restart;
						/*
						sw.Restart();
						count = 0;
						deviationMinOps = double.PositiveInfinity;
						deviationMin2Ops = double.PositiveInfinity;
						deviationMaxOps = 0;
						deviationMax2Ops = 0;
						deviationTotalBatches = 0;
						deviationTotalDuration = default;
						deviationMin2OpsDuration = default;
						deviationMax2OpsDuration = default;
						*/
					}
				}
				deviationTotalDuration += batchEnd - batchStart;
				// deviationTotalOps += batchOps;
				deviationTotalBatches++; // for avg
			}
			else
			{
				// batch sizing...
				if (((batchEnd - batchStart) < targetBatchDuration))
				{
					autoBatchBumps++;
					const double speedUpCoefficient = 1.25;
					var bumpFactor = Math.Max(2, speedUpCoefficient * targetBatchDuration.TotalMilliseconds / (batchEnd - batchStart).TotalMilliseconds); // not less than x2 for exponential grows
					if (bumpFactor == double.PositiveInfinity)
					{
						bumpFactor = 2;
					}
					batchSize = (int)(batchSize * bumpFactor);
					if (actualParameters.Trace)
					{
						Trace.WriteLine($"[{sw.ElapsedMilliseconds,4}] auto batch bump #{autoBatchBumps} batchDuration={(batchEnd - batchStart).TotalMilliseconds}ms targetBatchDuration={targetBatchDuration.TotalMilliseconds}ms batchSize={batchSize} bumpFactor={bumpFactor}");
					}
				}
				else
				{
					autoBatched = true;
					if (actualParameters.Trace)
					{
						Trace.WriteLine($"[{sw.ElapsedMilliseconds,4}] good batch size batchDuration={(batchEnd - batchStart).TotalMilliseconds}ms targetBatchDuration={targetBatchDuration.TotalMilliseconds}ms batchSize={batchSize}");
					}
					// now fit! We need to fit due to overshots, we need to overshot due to batch optimizations.
					batchSize = (int)(batchSize * targetBatchDuration.TotalMilliseconds / (batchEnd - batchStart).TotalMilliseconds);
					if (actualParameters.Trace)
					{
						Trace.WriteLine($"[{sw.ElapsedMilliseconds,4}] fit batch size batchSize={batchSize}");
					}
					// batch sizing stage done, reset
					goto restart;
					// sw.Restart();
					// count = 0;
				}
			}

			/*
			try
			{
				GC.EndNoGCRegion();
			}
			catch (Exception ex)
			{
			}
			*/

			batchIndex++;
		}
		sw.Stop();
		var ops = operationsCount / sw.Elapsed.TotalSeconds;
		if (deviationMin2OpsDuration != default)
		{
			deviationTotalBatches--;
			// deviationTotalOps -= deviationMin2Ops;
			deviationTotalDuration -= deviationMin2OpsDuration;
		}
		if (deviationMax2OpsDuration != default)
		{
			deviationTotalBatches--;
			// deviationTotalOps -= deviationMax2Ops;
			deviationTotalDuration -= deviationMax2OpsDuration;
		}
		var dev = (deviationMaxOps - deviationMinOps) / ops;
		// var opsDev = deviationTotalOps / deviationTotalBatches;

		// Very useful in unit tests - just to see all results in console
		// Console.WriteLine($"deviationMin2Ops={deviationMin2Ops} deviationMinOps={deviationMinOps}");
		// Console.WriteLine($"deviationMax2Ops={deviationMax2Ops} deviationMaxOps={deviationMaxOps}");
		var msg = $"ops={ops.ToString(ops > 2000 ? "N0" : "N2")} deviation={dev:P2} FinalBatchSize={batchSize} BatchCount={batchIndex} AutoBatchBumps={autoBatchBumps} ms={sw.ElapsedMilliseconds} count={operationsCount}";
		if (actualParameters.Trace)
		{
			Trace.WriteLine(msg);
		}
		Console.WriteLine(msg);
		var maxDev = actualParameters.MaxAcceptableDeviationFactor;
		if (dev > maxDev)
		{
			throw new Exception($"Performance Deviation is {dev:P2} which is more than {maxDev:P2}. OpS={ops:N2}. Unable to reliably measure performance of this flaky action.");
		}

		// SetThreadAffinity(uint.MaxValue);
		// ThreadPool.SetMinThreads(iw, icp); ThreadPool.SetMaxThreads(aw, acp);

		return new PerformanceResults
		{
			OperationsPerSecond = ops,
			DeviationFactor = dev,
		};
	}
	/*

Case 1. Neutral:

|Iteration|BatchSize|Duration| Elapsed | Count |
|--------:|--------:|-------:|--------:|------:|
|        1|        1|       1|        1|      1|
|        2|        2|       2|        3|      3|
|        3|        4|       4|        7|      7|
|        4|        8|       8|       15|     15|
|        5|       16|      16|       31|     31|
|        6|       32|      32|       63|     63|
|        7|       64|      64|      127|    127|
|        8|      128|     128|      255|    255|
|        9|      256|     256|      511|    511|
|       10|      512|     512|     1023|   1023| <-- stop

Case 2. Slow:

|Iteration|BatchSize|Duration| Elapsed | Count |
|--------:|--------:|-------:|--------:|------:|
|        1|        1|     200|      200|      1|
|        2|        2|     400|      600|      3|
|        3|        4|     800|     1400|      7| <-- stop


Case 3. Fast:
|Iteration|BatchSize|Duration| Elapsed | Count |
|--------:|--------:|-------:|--------:|------:|
|        1|        1|     0.1|      0.1|      1|
|        2|        2|     0.2|      0.3|      3|
|        3|        4|     0.4|      0.7|      7|
|        4|        8|     0.8|      1.5|     15|
|        5|       16|     1.6|      3.1|     31|
|        6|       32|     3.2|      6.3|     63|
|        7|       64|     6.4|     12.7|    127|
|        8|      128|    12.8|     25.5|    255|
|        9|      256|    25.6|     51.1|    511|
|       10|      512|    51.2|    102.3|   1023|
|       11|     1024|   102.4|    204.7|   2047|
|       12|     2048|   204.8|    409.5|   4095|
|       13|     4096|   409.6|    819.1|   8191| <-- no longer bump batch, exceed 400
|       14|     4096|   409.6|   1228.7|  12287| <-- stop

	*/

}
