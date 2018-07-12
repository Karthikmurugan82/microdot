﻿#region Copyright 
// Copyright 2017 Gigya Inc.  All rights reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License"); 
// you may not use this file except in compliance with the License.  
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDER AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
// ARE DISCLAIMED.  IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE
// LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
// CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
// SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
// INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
// CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
// ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
// POSSIBILITY OF SUCH DAMAGE.
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gigya.Common.Contracts.Exceptions;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.ServiceDiscovery.HostManagement;
using Gigya.Microdot.SharedLogic.Events;
using Gigya.Microdot.SharedLogic.Monitor;
using Gigya.Microdot.SharedLogic.Rewrite;
using Metrics;

namespace Gigya.Microdot.ServiceDiscovery.Rewrite
{    
    /// <summary>
    /// Provides a reachable node for each call to <see cref="GetNode"/>
    /// </summary>
    internal sealed class LoadBalancer: ILoadBalancer
    {
        private IDiscovery Discovery { get; }
        private ReachabilityCheck ReachabilityCheck { get; }
        private TrafficRoutingStrategy TrafficRoutingStrategy { get; }
        private Func<Node, DeploymentIdentifier, ReachabilityCheck, Action, NodeMonitoringState> CreateNodeMonitoringState { get; }
        private IDateTime DateTime { get; }
        private ILog Log { get; }
        private DeploymentIdentifier DeploymentIdentifier { get; }
        private Exception LastException { get; set; }

        private Node[] _lastDiscoveredNodes;
        private Node[] _reachableNodes;
        private NodeMonitoringState[] _nodesMonitoringState = new NodeMonitoringState[0];
        private readonly ComponentHealthMonitor _healthMonitor;
        private HealthCheckResult _healthStatus = HealthCheckResult.Healthy("Initializing...");

        int _disposed = 0;
        private bool _isMonitoring = true;
        private readonly object _lock = new object();
        private bool _wasUndeployed;



        public LoadBalancer(
            IDiscovery discovery,
            DeploymentIdentifier deploymentIdentifier, 
            ReachabilityCheck reachabilityCheck,
            TrafficRoutingStrategy trafficRoutingStrategy,
            Func<Node, DeploymentIdentifier, ReachabilityCheck, Action, NodeMonitoringState> createNodeMonitoringState,
            IHealthMonitor healthMonitor,
            IDateTime dateTime, 
            ILog log)
        {
            DeploymentIdentifier = deploymentIdentifier;
            Discovery = discovery;
            ReachabilityCheck = reachabilityCheck;
            TrafficRoutingStrategy = trafficRoutingStrategy;
            CreateNodeMonitoringState = createNodeMonitoringState;
            DateTime = dateTime;
            Log = log;            
            _healthMonitor = healthMonitor.SetHealthFunction(DeploymentIdentifier.ToString(), ()=>_healthStatus);
        }



        public async Task<Node> GetNode()
        {
            await LoadNodesFromSource().ConfigureAwait(false);
            var nodes = _nodesMonitoringState;
            if (!nodes.Any())
                throw new ServiceUnreachableException("No nodes were discovered for service", 
                    LastException,
                    unencrypted: new Tags
                    {
                        {"deploymentIdentifier", DeploymentIdentifier.ToString()},                        
                    });

            var reachableNodes = _reachableNodes; // get current state of reachable nodes
            if (!reachableNodes.Any())
                throw new ServiceUnreachableException("All nodes are unreachable",
                    nodes.FirstOrDefault(n=>n.LastException!=null)?.LastException,
                    unencrypted: new Tags
                    {
                        {"deploymentIdentifier", DeploymentIdentifier.ToString()},                        
                        {"nodes", string.Join(",", nodes.Select(n=>n.Node.ToString()))}                    
                    });
                        
            var index = GetIndexByTrafficRoutingStrategy();            
            return reachableNodes[index % reachableNodes.Length];
        }



        private int _roundRobinIndex = 0;

        private uint GetIndexByTrafficRoutingStrategy()
        {
            switch (TrafficRoutingStrategy)
            {
                case TrafficRoutingStrategy.RoundRobin:                    
                    return (uint)Interlocked.Increment(ref _roundRobinIndex);
                case TrafficRoutingStrategy.RandomByRequestID:
                    return (uint?)TracingContext.TryGetRequestID()?.GetHashCode() ?? (uint)Interlocked.Increment(ref _roundRobinIndex);
                default:
                    throw new ProgrammaticException($"The {nameof(TrafficRoutingStrategy)} '{TrafficRoutingStrategy}' is not supported by LoadBalancer.");
            }
        }



        public async Task<bool> WasUndeployed()
        {
            await LoadNodesFromSource().ConfigureAwait(false);
            return _wasUndeployed;
        }



        private void SetReachableNodes()
        {
            if (!_isMonitoring)
                return;

            lock (_lock)
            {
                var reachableNodes = _nodesMonitoringState.Where(s => s.IsReachable).Select(s => s.Node).ToArray();
                _healthStatus = CheckHealth(_nodesMonitoringState, reachableNodes);
                _reachableNodes = reachableNodes;
            }
        }



        private async Task LoadNodesFromSource()
        {
            if (_disposed>0)
                return;

            try
            {
                var sourceNodes = await Discovery.GetNodes(DeploymentIdentifier).ConfigureAwait(false);
                if (sourceNodes == null)
                {
                    _wasUndeployed = true;
                    StopMonitoring();
                    return;
                }

                if (sourceNodes == _lastDiscoveredNodes)
                    return;

                var newNodes = sourceNodes.Select(CreateState).ToArray();

                lock (_lock)
                {
                    var oldNodes = _nodesMonitoringState;
                    var nodesToRemove = oldNodes.Except(newNodes).ToArray();

                    _nodesMonitoringState = oldNodes.Except(nodesToRemove).Union(newNodes).ToArray();
                    StopMonitoringNodes(nodesToRemove);
                    _lastDiscoveredNodes = sourceNodes;
                    SetReachableNodes();
                }
            }
            catch (Exception ex)
            {
                LastException = ex;
            }
        }



        private NodeMonitoringState CreateState(Node node)
        {
            return CreateNodeMonitoringState(node, DeploymentIdentifier, ReachabilityCheck, SetReachableNodes);
        }



        private HealthCheckResult CheckHealth(NodeMonitoringState[] nodesState, Node[] reachableNodes)
        {            
            if (nodesState.Length == 0)
                return HealthCheckResult.Unhealthy($"No nodes were discovered");

            if (reachableNodes.Length==nodesState.Length)
                return HealthCheckResult.Healthy($"All {nodesState.Length} nodes are reachable");

            var unreachableNodes = nodesState.Where(n => !reachableNodes.Contains(n.Node));
            string message = string.Join("\r\n", unreachableNodes.Select(n=> $"    {n.Node.ToString()} - {n.LastException?.Message}"));
            
            if (reachableNodes.Length==0)
                return HealthCheckResult.Unhealthy($"All {nodesState.Length} nodes are unreachable\r\n{message}");
            else     
                return HealthCheckResult.Healthy($"{reachableNodes.Length} nodes out of {nodesState.Length} are reachable. Unreachable nodes:\r\n{message}");
        }



        private void StopMonitoringNodes(IEnumerable<NodeMonitoringState> monitoredNodes)
        {
            foreach (var monitoredNode in monitoredNodes)
            {
                monitoredNode.StopMonitoring();
            }
        }



        public void ReportUnreachable(Node node, Exception ex)
        {
            var nodeState = _nodesMonitoringState.FirstOrDefault(s => s.Node.Equals(node));
            lock (_lock)
                nodeState?.ReportUnreachable(ex);
        }

        private void StopMonitoring()
        {
            lock (_lock)
            {
                _isMonitoring = false;
                StopMonitoringNodes(_nodesMonitoringState);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Increment(ref _disposed) != 1)
                return;

            StopMonitoringNodes(_nodesMonitoringState);
            
            _healthMonitor.Dispose();            
        }


    }
}