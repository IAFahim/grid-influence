using System;
using System.Collections.Generic;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests.Support
{
    public sealed class GiColumnsSession : IDisposable
    {
        public readonly GiScenario Scenario;
        public readonly GiScenarioWorld Setup;
        public readonly GiTables Tables;
        public GiRecorder Recorder;
        private readonly int _batchLimit;
        private readonly GiDesired[] _desired;
        private readonly bool[] _exists;
        private readonly bool[] _enabled;
        private readonly GiApplied[] _applied;
        private GiWorld.Token _token;

        public GiColumnsSession(GiScenario scenario, int batchLimit = GiApplyPass.BatchLimit)
        {
            Scenario = scenario;
            _batchLimit = batchLimit;
            Setup = GiScenarioWorld.Create(scenario.Profile);
            Tables = GiColumnsExecutor.TablesOf(Setup, scenario.Profile);
            _token = GiWorld.Capture(Setup.World);
            _desired = new GiDesired[scenario.EntityCount];
            _exists = new bool[scenario.EntityCount];
            _enabled = new bool[scenario.EntityCount];
            _applied = new GiApplied[scenario.EntityCount];
        }

        public GiRecorder Record(int capacity, int interval, IGiCodec codec)
        {
            Recorder = new GiRecorder(Setup.World, Tables, Scenario.Profile.ToSpec(), capacity, interval, codec);
            Recorder.Start();
            return Recorder;
        }

        public ulong[] Step(int t)
        {
            var pulses = new List<GiPulse>();
            var splats = new List<GiScenarioOp>();
            foreach (var op in Scenario.Ticks[t].Ops) GiColumnsExecutor.Fold(op, _desired, _exists, _enabled, pulses, splats);
            for (var e = 0; e < _desired.Length; e++) _desired[e].Want = _exists[e] && _enabled[e];

            var commands = new List<GiCommand>();
            for (var e = 0; e < _desired.Length; e++)
                GiColumnsExecutor.AppendPlan(commands, GiDiff.Plan(_desired[e], _applied[e], Tables, e, commands.Count));

            pulses.Sort(new GiPulseOrder());
            foreach (var pulse in pulses) GiColumnsExecutor.AppendPlan(commands, GiPulseExpand.Plan(pulse, Tables, commands.Count));

            var array = commands.ToArray();
            var results = new GiCommandResult[array.Length];
            var status = GiApplyPass.Apply(ref _token, Setup.World, array, results, new GiWorld.SourceCommand[array.Length],
                new GiWorld.SourceResult[array.Length], _batchLimit);
            if (status != GiApplyStatus.Applied) throw new InvalidOperationException("apply " + status);

            if (array.Length > 0) Recorder?.OnCommands(array, results);
            for (var i = 0; i < array.Length; i++)
            {
                var owner = array[i].Owner;
                if (owner < 0) continue;

                var fault = GiWriteBack.Fold(array[i], results[i], ref _applied[owner]);
                if (fault != GiFaultCode.None) throw new InvalidOperationException(array[i].Kind + " " + fault);
            }

            var rejected = GiColumnsExecutor.Sync(Setup.World, ref _token, _desired, _applied, Recorder);
            if (rejected != 0) throw new InvalidOperationException("sync rejected " + rejected);

            GiColumnsExecutor.EmitSplats(Setup, Tables, splats, Recorder);
            GiWorld.Process(Setup.World);
            Recorder?.OnProcessed();
            var hashes = new ulong[Setup.Layers.Length];
            for (var l = 0; l < hashes.Length; l++) hashes[l] = GiFieldHash.OfLayer(Setup.World, Setup.Layers[l]);
            return hashes;
        }

        public void Dispose()
        {
            Setup.Dispose();
        }
    }
}
