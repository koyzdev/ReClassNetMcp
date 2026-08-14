using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace ReClassNetMcp.Host
{
    internal sealed class Snapshot
    {
        public int Sequence { get; }

        public string Reason { get; }

        public DateTime TakenUtc { get; }

        public byte[] Payload { get; }

        public int ClassCount { get; }

        public Snapshot(int sequence, string reason, byte[] payload, int classCount)
        {
            Sequence = sequence;
            Reason = reason;
            TakenUtc = DateTime.UtcNow;
            Payload = payload;
            ClassCount = classCount;
        }

        public JObject Describe()
        {
            return new JObject
            {
                ["sequence"] = Sequence,
                ["reason"] = Reason,
                ["takenAt"] = TakenUtc.ToString("o"),
                ["classCount"] = ClassCount,
                ["bytes"] = Payload.Length
            };
        }
    }

    //
    // This exists because the host does not have an undo stack, a redo stack or even a
    // dirty flag, so an edit made over the wire is otherwise final. A snapshot is the
    // whole project serialised to memory, which is cheap enough at project sizes people
    // actually work with and is the only rollback primitive the host format gives us.
    // The ring is capped so a long agent session cannot grow without bound. Pushes come
    // from the UI thread and Describe is called from an HTTP worker, hence the lock.
    //
    internal sealed class SnapshotRing
    {
        private const int Capacity = 16;

        private readonly object sync = new object();

        private readonly LinkedList<Snapshot> entries = new LinkedList<Snapshot>();

        private int sequence;

        public void Push(string reason, byte[] payload, int classCount)
        {
            lock (sync)
            {
                entries.AddLast(new Snapshot(++sequence, reason, payload, classCount));

                while (entries.Count > Capacity)
                {
                    entries.RemoveFirst();
                }
            }
        }

        public bool TryPop(out Snapshot snapshot)
        {
            lock (sync)
            {
                if (entries.Count == 0)
                {
                    snapshot = null;
                    return false;
                }

                snapshot = entries.Last.Value;
                entries.RemoveLast();
                return true;
            }
        }

        public JArray Describe()
        {
            var array = new JArray();

            lock (sync)
            {
                var node = entries.Last;
                while (node != null)
                {
                    array.Add(node.Value.Describe());
                    node = node.Previous;
                }
            }

            return array;
        }
    }
}
