﻿using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Reporting;
using System.Collections.Generic;


namespace ScenarioMeasurement
{
    public class TimeToMainParser : IParser
    {
        public void EnableKernelProvider(ITraceSession kernel)
        {
            kernel.EnableKernelProvider(TraceSessionManager.KernelKeyword.Process, TraceSessionManager.KernelKeyword.Thread, TraceSessionManager.KernelKeyword.ContextSwitch);
        }

        public void EnableUserProviders(ITraceSession user)
        {
            user.EnableUserProvider(TraceSessionManager.ClrKeyword.Startup);
        }

        public IEnumerable<Counter> Parse(string mergeTraceFile, string processName, IList<int> pids, string commandLine, Logger logger)
        {
            var results = new List<double>();
            double threadTime = 0;
            var threadTimes = new List<double>();
            var ins = new Dictionary<int, double>();
            double start = -1;
            int? pid = null;
            using (var source = new TraceSourceManager(mergeTraceFile))
            {

                source.Kernel.ProcessStart += evt =>
                {
                    logger.Log("PID(start): " + pid + ", Evt: " + evt + ", Evt. PID" + evt.ProcessID);
                    if (!pid.HasValue && ParserUtility.MatchProcessStart(evt, source, processName, pids, commandLine))
                    {
                        pid = evt.ProcessID;
                        logger.Log("Set PID");
                        start = evt.TimeStampRelativeMSec;
                    }
                };

                if (source.IsWindows)
                {
                    ((ETWTraceEventSource)source.Source).Kernel.ThreadCSwitch += evt =>
                    {
                        if (!pid.HasValue) // we're currently in a measurement interval
                            return;

                        if (evt.NewProcessID != pid && evt.OldProcessID != pid)
                            return; // but this isn't our process

                        if (evt.OldProcessID == pid) // this is a switch out from our process
                        {
                            if (ins.TryGetValue(evt.OldThreadID, out var value)) // had we ever recorded a switch in for this thread?
                            {
                                threadTime += evt.TimeStampRelativeMSec - value;
                                ins.Remove(evt.OldThreadID);
                            }
                        }
                        else // this is a switch in to our process
                        {
                            ins[evt.NewThreadID] = evt.TimeStampRelativeMSec;
                        }
                    };
                }

                ClrPrivateTraceEventParser clrpriv = new ClrPrivateTraceEventParser(source.Source);
                clrpriv.StartupMainStart += evt =>
                {
                    logger.Log("PID: " + pid + ", Evt: " + evt + ", " + evt.Log());
                    if(pid.HasValue && ParserUtility.MatchSingleProcessID(evt, source, (int)pid))
                    {
                        results.Add(evt.TimeStampRelativeMSec - start);
                        pid = null;
                        start = 0;
                        if (source.IsWindows)
                        {
                            threadTimes.Add(threadTime);
                            threadTime = 0;
                        }
                    }
                };
                source.Process();
            }

            var ret = new List<Counter> { new Counter() { Name = "Time To Main", Results = results.ToArray(), TopCounter = true, DefaultCounter = true, HigherIsBetter = false, MetricName = "ms" } };
            if (threadTimes.Count != 0)
            {
                ret.Add(new Counter() { Name = "Time on Thread", Results = threadTimes.ToArray(), TopCounter = true, DefaultCounter = false, HigherIsBetter = false, MetricName = "ms" });
            }
            return ret;

        }
    }
}
