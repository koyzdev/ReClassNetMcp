using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ReClassNET.Nodes;
using ReClassNetMcp.Tools;

namespace ReClassNetMcp.Model
{
    //
    // ClassNode.Uuid is the only stable identity anywhere in the host model. Plain nodes
    // carry nothing of the sort, not a name that has to be unique and not an id, so the
    // only way to name one is by where it sits. A handle is therefore a class uuid plus
    // the child indices walked from that class:
    //
    // <uuid>            the class itself
    // <uuid>:2          third child of the class
    // <uuid>:2/0/5      through nested containers, a wrapper always contributing 0
    //
    // The consequence is that a handle is a position and not a name. Inserting or
    // removing anything ahead of it silently makes it point somewhere else, so handles
    // are only good until the next edit and callers are told to re-read the class.
    //
    internal sealed class NodeHandle
    {
        public Guid ClassUuid { get; }

        public IReadOnlyList<int> Path { get; }

        public bool IsClass => Path.Count == 0;

        public NodeHandle(Guid classUuid, IReadOnlyList<int> path)
        {
            ClassUuid = classUuid;
            Path = path ?? new int[0];
        }

        public static NodeHandle Parse(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidArgumentsException("A node handle must not be empty");
            }

            var separator = value.IndexOf(':');
            var uuidText = separator < 0 ? value : value.Substring(0, separator);

            if (!Guid.TryParse(uuidText, out var uuid))
            {
                throw new InvalidArgumentsException($"'{value}' does not start with a class uuid");
            }

            if (separator < 0)
            {
                return new NodeHandle(uuid, new int[0]);
            }

            var remainder = value.Substring(separator + 1);
            if (remainder.Length == 0)
            {
                return new NodeHandle(uuid, new int[0]);
            }

            var parts = remainder.Split('/');
            var path = new int[parts.Length];

            for (var i = 0; i < parts.Length; ++i)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out path[i]) || path[i] < 0)
                {
                    throw new InvalidArgumentsException($"'{value}' contains an invalid child index '{parts[i]}'");
                }
            }

            return new NodeHandle(uuid, path);
        }

        //
        // Walks ParentNode upwards until it reaches the owner, then reverses what it
        // collected, since the tree only has parent links. Null means the node is not
        // addressable from this class: it was detached, it lives under a different class,
        // or a parent turned out to be neither a container nor a wrapper. Callers have to
        // treat null as "no handle" rather than as a failure worth reporting.
        //
        public static string Format(ClassNode owner, BaseNode node)
        {
            if (owner == null)
            {
                return null;
            }

            if (node == null || ReferenceEquals(owner, node))
            {
                return owner.Uuid.ToString("D");
            }

            var indices = new List<int>();
            var current = node;

            while (current != null && !ReferenceEquals(current, owner))
            {
                var parent = current.ParentNode;
                if (parent == null)
                {
                    return null;
                }

                if (parent is BaseContainerNode container)
                {
                    var index = container.FindNodeIndex(current);
                    if (index < 0)
                    {
                        return null;
                    }

                    indices.Add(index);
                }
                else if (parent is BaseWrapperNode)
                {
                    indices.Add(0);
                }
                else
                {
                    return null;
                }

                current = parent;
            }

            if (current == null)
            {
                return null;
            }

            indices.Reverse();

            var builder = new StringBuilder(owner.Uuid.ToString("D"));
            for (var i = 0; i < indices.Count; ++i)
            {
                builder.Append(i == 0 ? ':' : '/');
                builder.Append(indices[i].ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        public override string ToString()
        {
            if (IsClass)
            {
                return ClassUuid.ToString("D");
            }

            var builder = new StringBuilder(ClassUuid.ToString("D"));
            for (var i = 0; i < Path.Count; ++i)
            {
                builder.Append(i == 0 ? ':' : '/');
                builder.Append(Path[i].ToString(CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }
}
