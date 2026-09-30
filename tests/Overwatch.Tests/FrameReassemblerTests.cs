using System.Text.Json;
using Overwatch.Decode;
using Overwatch.Sample;
using Overwatch.SelfTest;

namespace Overwatch.Tests;

public class FrameReassemblerTests
{
    private static RuleSet Rules() => RuleCompiler.Compile(RuleLoader.Parse(SampleRules.Yaml));

    [Fact]
    public void Reassembles_a_frame_split_across_reads()
    {
        var reassembler = new FrameReassembler(65_536);
        var conversation = new ConversationState();
        var hello = FictionalProtocol.Hello(4, 8, 1);
        var rules = Rules();

        var first = reassembler.Push(hello.AsSpan(0, 3), rules, Direction.ClientToServer, conversation, 32);
        var second = reassembler.Push(hello.AsSpan(3, 2), rules, Direction.ClientToServer, conversation, 32);
        var third = reassembler.Push(hello.AsSpan(5), rules, Direction.ClientToServer, conversation, 32);

        Assert.Empty(first);
        Assert.Empty(second);
        var frame = Assert.Single(third);
        Assert.Equal("session_hello", frame.RuleId);
        Assert.Equal("4", frame.Fields["proto"]);
        Assert.Equal("8", frame.Fields["seq"]);
        Assert.Equal(0, reassembler.Buffered);
    }

    [Fact]
    public void Extracts_two_frames_from_one_segment()
    {
        var reassembler = new FrameReassembler(65_536);
        var conversation = new ConversationState();
        var rules = Rules();
        var first = FictionalProtocol.Hello(1, 1, 1);
        var second = FictionalProtocol.Hello(1, 2, 1);
        var combined = first.Concat(second).ToArray();

        var frames = reassembler.Push(combined, rules, Direction.ClientToServer, conversation, 32);

        Assert.Equal(2, frames.Count);
        Assert.Equal("1", frames[0].Fields["seq"]);
        Assert.Equal("2", frames[1].Fields["seq"]);
        Assert.Equal(0, reassembler.Buffered);
    }

    [Fact]
    public void Skips_a_garbage_byte_then_locks_onto_the_header()
    {
        var reassembler = new FrameReassembler(65_536);
        var conversation = new ConversationState();
        var hello = FictionalProtocol.Hello(9, 3, 2);
        var noisy = new byte[] { 0x00, 0xFF }.Concat(hello).ToArray();

        var frames = reassembler.Push(noisy, Rules(), Direction.ClientToServer, conversation, 32);

        var frame = Assert.Single(frames);
        Assert.Equal("9", frame.Fields["proto"]);
        Assert.True(reassembler.ResyncBytes >= 2);
    }

    [Fact]
    public void Waits_when_the_length_field_is_incomplete()
    {
        var reassembler = new FrameReassembler(65_536);
        var conversation = new ConversationState();
        conversation.Apply(Rules().Rules.Single(rule => rule.Id == "session_hello"));
        var tick = FictionalProtocol.Tick(1, 10, 20);

        var partial = reassembler.Push(tick.AsSpan(0, 3), Rules(), Direction.ServerToClient, conversation, 32);
        Assert.Empty(partial);
        Assert.Equal(3, reassembler.Buffered);

        var complete = reassembler.Push(tick.AsSpan(3), Rules(), Direction.ServerToClient, conversation, 32);
        var frame = Assert.Single(complete);
        Assert.Equal("10", frame.Fields["item_id"]);
        Assert.Equal("20", frame.Fields["price"]);
    }

    [Fact]
    public void Does_not_emit_a_tick_before_the_session_flag()
    {
        var reassembler = new FrameReassembler(65_536);
        var conversation = new ConversationState();
        var tick = FictionalProtocol.Tick(1, 5, 6);

        var frames = reassembler.Push(tick, Rules(), Direction.ServerToClient, conversation, 32);

        Assert.Empty(frames);
        Assert.Equal(0, reassembler.Buffered);
    }

    [Fact]
    public void Shares_conversation_context_between_directions()
    {
        var client = new FrameReassembler(65_536);
        var server = new FrameReassembler(65_536);
        var conversation = new ConversationState();
        var rules = Rules();
        var hello = FictionalProtocol.Hello(1, 1, 1);
        var tick = FictionalProtocol.Tick(4, 0x01020304, 99);

        Assert.NotEmpty(client.Push(hello, rules, Direction.ClientToServer, conversation, 32));
        var frames = server.Push(tick, rules, Direction.ServerToClient, conversation, 32);

        var frame = Assert.Single(frames);
        Assert.Equal("16909060", frame.Fields["item_id"]);
        Assert.Equal("99", frame.Fields["price"]);
    }
}
