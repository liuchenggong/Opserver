using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StackExchange.Opserver.Data
{
    public static class PollingEngine
    {
        private static readonly object _addLock = new object();
        private static readonly object _pollAllLock = new object();
        public static readonly HashSet<PollNode> AllPollNodes = new HashSet<PollNode>();

        private static Thread _globalPollingThread;
        private static volatile bool _shuttingDown;
        private static long _totalPollIntervals;
        internal static long _activePolls;
        private static DateTime? _lastPollAll;
        private static DateTime _startTime;
        private static Action<Func<Task>> _taskRunner;

        public static void Configure(Action<Func<Task>> taskRunner)
        {
            _taskRunner = taskRunner;
        }

        static PollingEngine()
        {
            StartPolling();
        }

        /// <summary>
        /// Adds a node to the global polling list ONLY IF IT IS NEW
        /// If a node with the same unique key was already added, it will not be added again
        /// </summary>
        /// <param name="node">The node to add to the global polling list</param>
        /// <returns>Whether the node was added</returns>
        public static bool TryAdd(PollNode node)
        {
            lock (_addLock)
            {
                return AllPollNodes.Add(node);
            }
        }

        public static bool TryRemove(PollNode node)
        {
            if (node == null || !node.AddedToGlobalPollers) return false;
            lock (_addLock)
            {
                return AllPollNodes.Remove(node);
            }
        }

        /// <summary>
        /// What do you think it does?
        /// </summary>
        public static void StartPolling()
        {
            _startTime = DateTime.UtcNow;
            _globalPollingThread = _globalPollingThread ?? new Thread(MonitorPollingLoop)
                {
                    Name = "GlobalPolling",
                    Priority = ThreadPriority.Lowest,
                    IsBackground = true
                };
            if (!_globalPollingThread.IsAlive)
                _globalPollingThread.Start();
        }

        /// <summary>
        /// Performs a soft shutdown after the current poll finishes
        /// </summary>
        public static void StopPolling()
        {
            _shuttingDown = true;
        }

        public class GlobalPollingStatus : IMonitorStatus
        {
            public MonitorStatus MonitorStatus { get; internal set; }
            public string MonitorStatusReason { get; internal set; }
            public DateTime StartTime { get; internal set; }
            public DateTime? LastPollAll { get; internal set; }
            public bool IsAlive { get; internal set; }
            public long TotalPollIntervals { get; internal set; }
            public long ActivePolls { get; internal set; }
            public int NodeCount { get; internal set; }
            public int TotalPollers { get; internal set; }
            public List<Tuple<Type, int>> NodeBreakdown { get; internal set; }
            public List<PollNode> Nodes { get; internal set; }
        }

        public static GlobalPollingStatus GetPollingStatus()
        {
            return new GlobalPollingStatus
                {
                    MonitorStatus = _globalPollingThread.IsAlive ? (AllPollNodes.Count > 0 ? MonitorStatus.Good : MonitorStatus.Unknown) : MonitorStatus.Critical,
                    MonitorStatusReason = _globalPollingThread.IsAlive ? (AllPollNodes.Count > 0 ? null : "No Poll Nodes") : "Global Polling Thread Dead",
                    StartTime = _startTime,
                    LastPollAll = _lastPollAll,
                    IsAlive = _globalPollingThread.IsAlive,
                    TotalPollIntervals = _totalPollIntervals,
                    ActivePolls = _activePolls,
                    NodeCount = AllPollNodes.Count,
                    TotalPollers = AllPollNodes.Sum(n => n.DataPollers.Count()),
                    NodeBreakdown = AllPollNodes.GroupBy(n => n.GetType()).Select(g => Tuple.Create(g.Key, g.Count())).ToList(),
                    Nodes = AllPollNodes.ToList()
                };
        }

        private static void MonitorPollingLoop()
        {
            while (!_shuttingDown)
            {
                try
                {
                    StartPollLoop();
                }
                catch (ThreadAbortException e)
                {
                    if (!_shuttingDown)
                        Current.LogException("Global polling loop shutting down", e);
                }
                catch (Exception ex)
                {
                    Current.LogException(ex);
                }
                try
                {
                    Thread.Sleep(2000);
                }
                catch (ThreadAbortException)
                {
                    // application is cycling, AND THAT'S OKAY
                }
            }
        }

        private static void StartPollLoop()
        {
            while (!_shuttingDown)
            {
                PollAllAndForget();
                Thread.Sleep(1000);
            }
        }

        public static void PollAllAndForget()
        {
            if (!Monitor.TryEnter(_pollAllLock, 500)) return;

            Interlocked.Increment(ref _totalPollIntervals);
            try
            {
                foreach (var n in AllPollNodes)
                {
                    if (n.IsPolling || !n.NeedsPoll)
                    {
                        continue;
                    }
                    _taskRunner?.Invoke(() => n.PollAsync());
                }
            }
            catch (Exception e)
            {
                Current.LogException(e);
            }
            finally
            {
                Monitor.Exit(_pollAllLock);
            }
            _lastPollAll = DateTime.UtcNow;
        }

        /// <summary>
        /// Polls all caches on a specific PollNode
        /// </summary>
        /// <param name="nodeType">Type of node to poll</param>
        /// <param name="key">Unique key of the node to poll</param>
        /// <param name="cacheGuid">If included, the specific cache to poll</param>
        /// <returns>Whether the poll was successful</returns>
        public static async Task<bool> PollAsync(string nodeType, string key, Guid? cacheGuid = null)
        {
            if (nodeType == Cache.TimedCacheKey)
            {
                Cache.Purge(key);
                return true;
            }

            var node = AllPollNodes.FirstOrDefault(p => p.NodeType == nodeType && p.UniqueKey == key);
            if (node == null) return false;

            if (cacheGuid.HasValue)
            {
                var cache = node.DataPollers.FirstOrDefault(p => p.UniqueId == cacheGuid);
                if (cache != null)
                {
                    await cache.PollGenericAsync(true).ConfigureAwait(false);
                }
                return cache?.LastPollSuccessful ?? false;
            }
            // Polling an entire server
            await node.PollAsync(true).ConfigureAwait(false);
            return true;
        }

        public static List<PollNode> GetNodes(string type)
        {
            return AllPollNodes.Where(pn => string.Equals(pn.NodeType, type, StringComparison.InvariantCultureIgnoreCase)).ToList();
        }

        public static PollNode GetNode(string type, string key)
        {
            return AllPollNodes.FirstOrDefault(pn => string.Equals(pn.NodeType, type, StringComparison.InvariantCultureIgnoreCase) && pn.UniqueKey == key);
        }

        public static Cache GetCache(Guid id)
        {
            foreach (var pn in AllPollNodes)
            {
                foreach (var c in pn.DataPollers)
                {
                    if (c.UniqueId == id) return c;
                }
            }
            return null;
        }

        public static ThreadStats GetThreadStats() => new ThreadStats();

        public class ThreadStats
        {
            private readonly int _minWorkerThreads;
            public int MinWorkerThreads => _minWorkerThreads;

            private readonly int _minIOThreads;
            public int MinIOThreads => _minIOThreads;

            private readonly int _availableWorkerThreads;
            public int AvailableWorkerThreads => _availableWorkerThreads;

            private readonly int _availableIOThreads;
            public int AvailableIOThreads => _availableIOThreads;

            private readonly int _maxIOThreads;
            public int MaxIOThreads => _maxIOThreads;

            private readonly int _maxWorkerThreads;
            public int MaxWorkerThreads => _maxWorkerThreads;

            public int BusyIOThreads => _maxIOThreads - _availableIOThreads;
            public int BusyWorkerThreads => _maxWorkerThreads - _availableWorkerThreads;

            public ThreadStats()
            {
                ThreadPool.GetMinThreads(out _minWorkerThreads, out _minIOThreads);
                ThreadPool.GetAvailableThreads(out _availableWorkerThreads, out _availableIOThreads);
                ThreadPool.GetMaxThreads(out _maxWorkerThreads, out _maxIOThreads);
            }
        }
    }
}
