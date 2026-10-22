using System.Reflection;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Cotabby.Win32.Insertion;
using Microsoft.Extensions.Logging.Abstractions;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Tests;

public sealed class SendInputTextInserterEmojiTests
{
    [Fact]
    public void ReplacementValidatorConfiguresTheSameAutomationClientToFiniteTimeouts()
    {
        using var validator = new BoundedUiaReplacementFocusValidator();
        var client = (IUIAutomation2)typeof(BoundedUiaReplacementFocusValidator)
            .GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(validator)!;

        Assert.Equal(0, client.get_TransactionTimeout(out uint transactionTimeout));
        Assert.Equal(BoundedUiaReplacementFocusValidator.TransactionTimeoutMilliseconds, transactionTimeout);
        Assert.Equal(0, client.get_ConnectionTimeout(out uint connectionTimeout));
        Assert.Equal(BoundedUiaReplacementFocusValidator.TransactionTimeoutMilliseconds, connectionTimeout);
    }

    [Fact]
    public async Task SameFieldTypingAbcWhileReplacementWaitsForGateDispatchesNoInput()
    {
        var expected = Field("prefix", 6);
        FocusedField live = expected;
        int sendCalls = 0;
        var validator = new DelegateValidator((target, tail, caret) =>
            EmojiFocusPolicy.MatchesExpectedReplacement(target, live, tail, caret));
        var inserter = CreateInserter(validator, _ => true, (_, _, _) =>
        {
            Interlocked.Increment(ref sendCalls);
            return 1;
        });
        var gate = (SemaphoreSlim)typeof(SendInputTextInserter)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inserter)!;
        gate.Wait();
        Task<TextReplacementResult> replacement = inserter.ReplaceTextAsync(
            expected, 7, "X", ":smile:", 13, CancellationToken.None);
        live = expected with { Text = "prefix:smile:abc", CaretOffset = 16 };
        gate.Release();

        var result = await replacement.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TextReplacementStatus.Rejected, result.Status);
        Assert.Equal(0, sendCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PostBlockUiaTimeoutOrFailureAlwaysUnblocksOnTheOwningThread(bool timeout)
    {
        int validations = 0;
        var validator = new DelegateValidator((_, _, _) =>
        {
            if (Interlocked.Increment(ref validations) == 1) return true;
            if (timeout) throw new TimeoutException("provider did not respond");
            throw new InvalidOperationException("provider call failed");
        });
        var calls = new List<(bool Block, int ThreadId)>();
        var inserter = CreateInserter(validator, block =>
        {
            calls.Add((block, Environment.CurrentManagedThreadId));
            return true;
        }, (_, _, _) => throw new InvalidOperationException("must not dispatch after failed validation"));

        var result = await inserter.ReplaceTextAsync(
            Field("prefix", 6), 7, "X", ":smile:", 13, CancellationToken.None);

        Assert.Equal(TextReplacementStatus.Rejected, result.Status);
        Assert.Equal(new[] { true, false }, calls.Select(call => call.Block));
        Assert.All(calls, call => Assert.Equal(calls[0].ThreadId, call.ThreadId));
    }

    [Fact]
    public async Task ShortSendInputCountIsPartialAndAttemptsMatchingKeyUpRecovery()
    {
        var validator = new DelegateValidator((_, _, _) => true);
        int calls = 0;
        INPUT? recoveryInput = null;
        var inserter = CreateInserter(validator, _ => true, (count, inputs, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Assert.Equal(16u, count);
                return 3;
            }

            Assert.Equal(1u, count);
            recoveryInput = inputs[0];
            return 1;
        });

        var result = await inserter.ReplaceTextAsync(
            Field("prefix", 6), 7, "X", ":smile:", 13, CancellationToken.None);

        Assert.Equal(TextReplacementStatus.Partial, result.Status);
        Assert.Equal(16u, result.RequestedInputCount);
        Assert.Equal(3u, result.InsertedInputCount);
        Assert.True(result.KeyReleaseRecovered);
        Assert.NotNull(recoveryInput);
        Assert.Equal(VK_BACK, recoveryInput!.Value.U.ki.wVk);
        Assert.Equal(KEYEVENTF_KEYUP, recoveryInput.Value.U.ki.dwFlags);
    }

    [Fact]
    public async Task ExactFocusedReplacementReportsCompleted()
    {
        var inserter = CreateInserter(
            new DelegateValidator((_, _, _) => true),
            _ => true,
            (count, _, _) => count);

        var result = await inserter.ReplaceTextAsync(
            Field("prefix", 6), 7, "X", ":smile:", 13, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(TextReplacementStatus.Completed, result.Status);
    }

    private static SendInputTextInserter CreateInserter(
        IEmojiReplacementFocusValidator validator,
        Func<bool, bool> blockInput,
        Func<uint, INPUT[], int, uint> sendInput) =>
        new(
            NullLogger<SendInputTextInserter>.Instance,
            () => validator,
            blockInput,
            sendInput);

    private static FocusedField Field(string text, int caretOffset) => new()
    {
        ElementHandle = new object(),
        StableFieldIdentity = "42.7.9",
        HasRuntimeIdIdentity = true,
        ProcessId = 42,
        ProcessName = "fixture",
        Text = text,
        CaretOffset = caretOffset,
        CaretRect = ScreenRect.Empty,
        FieldRect = new ScreenRect(10, 20, 200, 24),
        IsSingleLine = true,
        IsSecure = false,
        IsCredentialField = false,
    };

    private sealed class DelegateValidator(Func<FocusedField, string, int, bool> validate)
        : IEmojiReplacementFocusValidator
    {
        public bool Matches(FocusedField expected, string expectedTail, int expectedCaretOffset) =>
            validate(expected, expectedTail, expectedCaretOffset);

        public void Dispose() { }
    }
}
