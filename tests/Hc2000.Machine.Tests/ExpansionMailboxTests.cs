using System.Buffers.Binary;
using Hc2000.Machine.Expansion;
using Xunit;

namespace Hc2000.Machine.Tests;

// Pure mailbox fixtures only: retirement, origin, port decoding and snapshot adoption are separate gates.
public sealed class ExpansionMailboxTests
{
    private static byte[] Frame(ulong requestId, params byte[] payload)
    {
        var frame = new byte[73];
        frame[0] = checked((byte)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), requestId);
        payload.CopyTo(frame, 9);
        return frame;
    }

    private static void Stage(ExpansionMailbox mailbox, byte[] frame)
    {
        Assert.Equal(ExpansionMailboxEffect.None, mailbox.WriteLocalControl(1));
        foreach (var value in frame) mailbox.WriteLocalByte(value);
    }

    private static byte[] ReadInbox(ExpansionMailbox mailbox) =>
        Enumerable.Range(0, 73).Select(_ => mailbox.ReadLocalByte()).ToArray();

    [Fact]
    public void InboxIsCopiedBoundedAndRetainedUntilExplicitLocalAcknowledge()
    {
        var mailbox = new ExpansionMailbox();
        var frame = Frame(0x1122334455667788, 1, 2, 3);
        var expected = frame.ToArray();
        Assert.Equal(2, mailbox.ReadStatus());
        Assert.Equal(0xff, mailbox.ReadLocalByte());
        Assert.Equal(ExpansionResult.Accepted, mailbox.SendInbox(frame));
        frame[9] = 0xff;
        Assert.True(mailbox.HasInbox);
        Assert.Equal(3, mailbox.ReadStatus());
        Assert.Equal(expected[0], mailbox.ReadLocalByte());
        Assert.Equal(ExpansionResult.Busy, mailbox.SendInbox(Frame(99, 5)));
        Assert.Equal(expected[1], mailbox.ReadLocalByte()); // refusal did not rewind the cursor
        mailbox.WriteLocalControl(4);
        Assert.Equal(expected, ReadInbox(mailbox));
        Assert.Equal(0xff, mailbox.ReadLocalByte());
        Assert.True(mailbox.HasInbox); // end-of-stream is not acknowledgment
        mailbox.WriteLocalControl(4);
        Assert.Equal(expected, ReadInbox(mailbox));
        mailbox.WriteLocalControl(5);
        Assert.False(mailbox.HasInbox);
        Assert.Equal(0xff, mailbox.ReadLocalByte());
        Assert.Equal(ExpansionResult.Accepted, mailbox.SendInbox(Frame(99, 5)));
        Assert.Equal(Frame(99, 5), ReadInbox(mailbox));
    }

    [Fact]
    public void OutboxAndLocalStagingSurviveBusyUntilMatchingMainAcknowledgeAndExplicitRetry()
    {
        var mailbox = new ExpansionMailbox();
        var first = Frame(10, 0xa1, 0xb2);
        var second = Frame(11, 0xc3);
        Stage(mailbox, first);
        Assert.Equal(6, mailbox.ReadStatus());
        mailbox.WriteLocalControl(2);
        Assert.True(mailbox.HasOutbox);
        Assert.Equal(0, mailbox.ReadStatus());
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out var copy));
        Assert.Equal(first, copy);
        copy![9] = 0xff;
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out copy));
        Assert.Equal(first, copy); // host copy mutation and repeated read cannot consume it
        Stage(mailbox, second);
        mailbox.WriteLocalControl(2);
        Assert.Equal(0x0c, mailbox.ReadStatus()); // collecting plus local Busy, old TX still full
        Assert.Equal(ExpansionResult.InvalidState, mailbox.AcknowledgeOutbox(11));
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out copy));
        Assert.Equal(first, copy);
        Assert.Equal(ExpansionResult.Accepted, mailbox.AcknowledgeOutbox(10));
        Assert.False(mailbox.HasOutbox);
        mailbox.WriteLocalControl(2); // retry the intact second frame, without restaging
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out copy));
        Assert.Equal(second, copy);
        mailbox.WriteLocalControl(3); // abort cannot consume a committed frame
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out copy));
        Assert.Equal(second, copy);
        Assert.Equal(ExpansionResult.Accepted, mailbox.AcknowledgeOutbox(11));
        Assert.Equal(ExpansionResult.InvalidState, mailbox.AcknowledgeOutbox(11));
        Assert.Equal(ExpansionResult.Busy, mailbox.ReadOutbox(out copy));
        Assert.Null(copy);
    }

    [Fact]
    public void InvalidFramesAndOverflowNeverPublishOrOverwriteACommittedMessage()
    {
        var mailbox = new ExpansionMailbox();
        var invalid = new List<byte[]> { Array.Empty<byte>(), new byte[72], new byte[74], Frame(0) };
        var badLength = Frame(1);
        badLength[0] = 65;
        invalid.Add(badLength);
        var badPadding = Frame(2, 0xaa);
        badPadding[^1] = 1;
        invalid.Add(badPadding);
        foreach (var frame in invalid)
        {
            Assert.Equal(ExpansionResult.InvalidFrame, mailbox.SendInbox(frame));
            Assert.False(mailbox.HasInbox);
            Assert.Equal(2, mailbox.ReadStatus()); // root refusal does not poison local error
        }
        foreach (var frame in invalid.Where(frame => frame.Length <= 73))
        {
            Stage(mailbox, frame);
            mailbox.WriteLocalControl(2);
            Assert.False(mailbox.HasOutbox);
            Assert.Equal(0x1e, mailbox.ReadStatus()); // InvalidFrame plus collecting/TX-free
            mailbox.WriteLocalControl(3);
            Assert.Equal(2, mailbox.ReadStatus());
        }
        Stage(mailbox, Frame(5, 3));
        mailbox.WriteLocalByte(0); // exact-capacity overflow, even a zero byte is invalid
        mailbox.WriteLocalControl(2);
        Assert.False(mailbox.HasOutbox);
        Assert.Equal(0x1e, mailbox.ReadStatus());
        mailbox.WriteLocalControl(3);
        var maximum = Frame(ulong.MaxValue, Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        Stage(mailbox, maximum);
        mailbox.WriteLocalControl(2);
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out var copy));
        Assert.Equal(maximum, copy);
    }

    [Fact]
    public void UnknownAndUnframedWritesDoNotDiscardInboxOutboxOrCollectedBytes()
    {
        var mailbox = new ExpansionMailbox();
        var frame = Frame(1, 0x42);
        mailbox.SendInbox(frame);
        mailbox.WriteLocalByte(9); // no TX_BEGIN
        Assert.Equal(0x1b, mailbox.ReadStatus());
        Stage(mailbox, frame);
        mailbox.WriteLocalControl(0xff);
        Assert.Equal(0x1f, mailbox.ReadStatus());
        mailbox.WriteLocalControl(1); // nested begin is Busy, not an implicit abort
        Assert.Equal(0x0f, mailbox.ReadStatus());
        mailbox.WriteLocalControl(2);
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out var copy));
        Assert.Equal(frame, copy);
        Assert.Equal(frame, ReadInbox(mailbox));
    }

    [Fact]
    public void WaitAndIdleArePlainRetirementEffectsNotMailboxMutationOrAutomaticWake()
    {
        var mailbox = new ExpansionMailbox();
        Assert.Equal(ExpansionMailboxEffect.Wait, mailbox.WriteLocalControl(6));
        Assert.Equal(2, mailbox.ReadStatus());
        Assert.Equal(0x42, mailbox.ReadStatus(waiting: true));
        Assert.Equal(ExpansionResult.Accepted, mailbox.SendInbox(Frame(7)));
        Assert.Equal(ExpansionMailboxEffect.Wait, mailbox.WriteLocalControl(6));
        Assert.True(mailbox.HasInbox); // owner observes this when applying WAIT at retirement
        Assert.Equal(ExpansionMailboxEffect.Idle, mailbox.WriteLocalControl(7));
        Assert.Equal(0x83, mailbox.ReadStatus(serviceIdle: true));
        Assert.Equal(0xc3, mailbox.ReadStatus(waiting: true, serviceIdle: true)); // flags are caller-owned
        Assert.True(mailbox.HasInbox);
    }

    [Fact]
    public void ResetClearsBothDirectionsCursorsStagingAndLocalErrorsWithoutReallocatingTheProtocol()
    {
        var mailbox = new ExpansionMailbox();
        mailbox.SendInbox(Frame(22, 3));
        _ = mailbox.ReadLocalByte();
        Stage(mailbox, Frame(23, 4));
        mailbox.WriteLocalControl(2);
        Stage(mailbox, Frame(24, 5));
        mailbox.WriteLocalControl(2); // Busy plus a valid retryable stage
        mailbox.Clear();
        Assert.Equal(2, mailbox.ReadStatus());
        Assert.False(mailbox.HasInbox);
        Assert.False(mailbox.HasOutbox);
        Assert.Equal(0xff, mailbox.ReadLocalByte());
        Assert.Equal(ExpansionResult.Busy, mailbox.ReadOutbox(out var copy));
        Assert.Null(copy);
        mailbox.WriteLocalControl(2); // old staging cannot be committed after reset
        Assert.False(mailbox.HasOutbox);
        Assert.Equal(0x1a, mailbox.ReadStatus());
        Stage(mailbox, Frame(25)); // empty payload with nonzero request ID is valid
        mailbox.WriteLocalControl(2);
        Assert.Equal(ExpansionResult.Accepted, mailbox.ReadOutbox(out copy));
        Assert.Equal(Frame(25), copy);
    }
}
