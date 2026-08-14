using System;
using ReClassNET.Nodes;
using ReClassNetMcp.Model;
using ReClassNetMcp.Tools;
using Xunit;

namespace ReClassNetMcp.Tests
{
    public sealed class NodeHandleTests
    {
        private const string Sample = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

        [Fact]
        public void BareUuidRoundTrips()
        {
            var handle = NodeHandle.Parse(Sample);

            Assert.Equal(new Guid(Sample), handle.ClassUuid);
            Assert.True(handle.IsClass);
            Assert.Empty(handle.Path);
            Assert.Equal(Sample, handle.ToString());
        }

        [Fact]
        public void IndexPathRoundTrips()
        {
            var text = Sample + ":1/2/3";

            var handle = NodeHandle.Parse(text);

            Assert.Equal(new Guid(Sample), handle.ClassUuid);
            Assert.False(handle.IsClass);
            Assert.Equal(3, handle.Path.Count);
            Assert.Equal(1, handle.Path[0]);
            Assert.Equal(2, handle.Path[1]);
            Assert.Equal(3, handle.Path[2]);
            Assert.Equal(text, handle.ToString());
        }

        [Fact]
        public void TrailingSeparatorIsTreatedAsTheClassItself()
        {
            var handle = NodeHandle.Parse(Sample + ":");

            Assert.True(handle.IsClass);
            Assert.Equal(Sample, handle.ToString());
        }

        [Theory]
        [InlineData("")]
        [InlineData((string)null)]
        [InlineData("not-a-uuid")]
        [InlineData("not-a-uuid:0")]
        [InlineData(Sample + ":x")]
        [InlineData(Sample + ":1/x")]
        [InlineData(Sample + ":1//2")]
        [InlineData(Sample + ":-1")]
        [InlineData(Sample + ":1/-2")]
        [InlineData(Sample + ":1.5")]
        public void MalformedHandleIsRejected(string value)
        {
            Assert.Throws<InvalidArgumentsException>(() => NodeHandle.Parse(value));
        }

        [Fact]
        public void FormatReturnsTheBareUuidForTheClassItself()
        {
            var owner = ClassNode.Create();

            Assert.Equal(owner.Uuid.ToString("D"), NodeHandle.Format(owner, owner));
            Assert.Equal(owner.Uuid.ToString("D"), NodeHandle.Format(owner, null));
        }

        [Fact]
        public void FormatProducesTheIndexPathOfANestedNode()
        {
            var owner = ClassNode.Create();

            var first = BaseNode.CreateInstanceFromType(typeof(Hex32Node), true);
            owner.AddNode(first);

            var union = (UnionNode)BaseNode.CreateInstanceFromType(typeof(UnionNode), true);
            owner.AddNode(union);

            Assert.Single(union.Nodes);

            var nested = BaseNode.CreateInstanceFromType(typeof(Hex64Node), true);
            union.AddNode(nested);

            var uuid = owner.Uuid.ToString("D");

            Assert.Equal(uuid + ":0", NodeHandle.Format(owner, first));
            Assert.Equal(uuid + ":1", NodeHandle.Format(owner, union));
            Assert.Equal(uuid + ":1/0", NodeHandle.Format(owner, union.Nodes[0]));
            Assert.Equal(uuid + ":1/1", NodeHandle.Format(owner, nested));

            var parsed = NodeHandle.Parse(NodeHandle.Format(owner, nested));

            Assert.Equal(owner.Uuid, parsed.ClassUuid);
            Assert.Equal(2, parsed.Path.Count);
            Assert.Equal(1, parsed.Path[0]);
            Assert.Equal(1, parsed.Path[1]);
        }

        [Fact]
        public void FormatReturnsNullForANodeOutsideTheOwner()
        {
            var owner = ClassNode.Create();
            var other = ClassNode.Create();

            var stranger = BaseNode.CreateInstanceFromType(typeof(Hex32Node), true);
            other.AddNode(stranger);

            Assert.Null(NodeHandle.Format(owner, stranger));
            Assert.Null(NodeHandle.Format(null, stranger));
        }
    }
}
