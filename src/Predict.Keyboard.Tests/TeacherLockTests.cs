using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09 step 7: the teacher PIN (PBKDF2, rules, back-off persisted, recovery code, logs without digits).</summary>
public class TeacherLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kbpin-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _log = new();

    public TeacherLockTests() => TeacherLock.Iterations = 1000;

    public void Dispose()
    {
        TestDirs.Delete(_dir);
    }

    private TeacherLock Open()
    {
        var f = new JsonFileStore(Path.Combine(_dir, "teacher.json"), _log.Add, "profiles", schema: 1);
        f.Load();
        return new TeacherLock(f, () => _now, _log.Add);
    }

    [Theory]
    [InlineData("4826", null)]
    [InlineData("482601", null)]
    [InlineData("482", PinProblem.Length)]
    [InlineData("4826013", PinProblem.Length)]
    [InlineData("48a6", PinProblem.NotDigits)]
    [InlineData("４８２６", PinProblem.NotDigits)]   // full-width digits are not ASCII digits
    [InlineData("0000", PinProblem.Weak)]
    [InlineData("1234", PinProblem.Weak)]
    [InlineData("123456", PinProblem.Weak)]
    [InlineData("9876", PinProblem.Weak)]
    [InlineData("1212", PinProblem.Weak)]
    [InlineData("2580", PinProblem.Weak)]
    public void Pin_Rules(string pin, object? expected) => Assert.Equal((PinProblem?)expected, TeacherLock.Check(pin));

    [Theory]
    [InlineData(1, 0)]
    [InlineData(4, 0)]
    [InlineData(5, 30)]
    [InlineData(6, 60)]
    [InlineData(7, 120)]
    [InlineData(9, 480)]
    [InlineData(10, 900)]
    [InlineData(50, 900)]
    public void Back_Off_Doubles_Up_To_15_Minutes(int failures, int seconds) => Assert.Equal(seconds, TeacherLock.WaitFor(failures));

    [Fact]
    public void Hash_Stores_Its_Iterations_And_Verifies_In_Constant_Time()
    {
        var h = TeacherLock.Hash("4826", 1234);
        Assert.Equal(TeacherLock.Alg, h.Alg);
        Assert.Equal(1234, h.Iterations);
        Assert.Equal(16, Convert.FromBase64String(h.Salt).Length);
        Assert.Equal(32, Convert.FromBase64String(h.Hash).Length);
        Assert.True(TeacherLock.Matches("4826", h));
        Assert.False(TeacherLock.Matches("4827", h));
        Assert.False(TeacherLock.Matches("4826", h with { Iterations = 1235 }));
        Assert.False(TeacherLock.Matches("4826", h with { Salt = "not base64!" }));
        Assert.NotEqual(TeacherLock.Hash("4826", 1234).Salt, h.Salt);
        Assert.Equal(600_000, TeacherLock.DefaultIterations);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void Five_Wrong_Then_Wait_Kept_After_Restart()
    {
        var lk = Open();
        lk.SetPin("4826");
        for (int i = 1; i <= 4; i++)
        {
            var r = lk.Verify("1111");
            Assert.Equal(PinOutcome.Wrong, r.Outcome);
            Assert.Equal(i, r.Failures);
            Assert.Equal(0, r.WaitSeconds);
        }
        var fifth = lk.Verify("1111");
        Assert.Equal(30, fifth.WaitSeconds);
        // during the wait even the right PIN is not checked
        Assert.Equal(PinOutcome.Wait, lk.Verify("4826").Outcome);
        lk.Flush();
        var again = Open();                                     // a restart does not reset the wait
        Assert.Equal(30, again.WaitRemaining());
        Assert.Equal(PinOutcome.Wait, again.Verify("4826").Outcome);
        _now = _now.AddSeconds(31);
        Assert.Equal(0, again.WaitRemaining());
        var sixth = again.Verify("1111");
        Assert.Equal(60, sixth.WaitSeconds);
        _now = _now.AddSeconds(61);
        Assert.Equal(PinOutcome.Ok, again.Verify("4826").Outcome);
        Assert.Equal(0, again.Failures);
    }

    [Fact]
    public void Recovery_Code_Works_Normalised_And_Changes_With_A_New_Pin()
    {
        var lk = Open();
        var code = lk.SetPin("4826");
        Assert.Equal(PinOutcome.Ok, lk.Verify(code.ToLowerInvariant().Replace("-", " "), recovery: true).Outcome);
        Assert.Equal(PinOutcome.Wrong, lk.Verify("AAAA-BBBB-CCCC", recovery: true).Outcome);
        var code2 = lk.SetPin("7391");
        Assert.NotEqual(code, code2);
        Assert.Equal(PinOutcome.Wrong, lk.Verify(code, recovery: true).Outcome);
        Assert.Equal(PinOutcome.Ok, lk.Verify(code2, recovery: true).Outcome);
        var code3 = lk.NewRecovery();
        Assert.Equal(PinOutcome.Wrong, lk.Verify(code2, recovery: true).Outcome);
        Assert.Equal(PinOutcome.Ok, lk.Verify(code3, recovery: true).Outcome);
        Assert.Equal(PinOutcome.Ok, lk.Verify("7391").Outcome);   // the PIN is unchanged by a new code
        Assert.Equal("ABCD01", TeacherLock.NormalizeRecovery(" ab-cd 01 "));   // no mapping: 0 / 1 are not in the alphabet
    }

    [Fact]
    public void Clear_Removes_The_Pin()
    {
        var lk = Open();
        lk.SetPin("4826");
        lk.Clear();
        Assert.False(lk.HasPin);
        Assert.Equal(PinOutcome.NoPin, lk.Verify("4826").Outcome);
    }

    [Fact]
    public void Logs_And_File_Never_Contain_The_Pin_Or_The_Code()
    {
        var lk = Open();
        var code = lk.SetPin("482617");
        lk.Verify("482617");
        lk.Verify("917355");
        lk.Verify(code, recovery: true);
        for (int i = 0; i < 5; i++) lk.Verify("917355");
        lk.Flush();
        Assert.Contains(_log, l => l.StartsWith("pin: verified"));
        Assert.Contains(_log, l => l.StartsWith("pin: wrong"));
        Assert.Contains(_log, l => l.StartsWith("pin: changed"));
        Assert.Contains(_log, l => l.Contains("wait"));
        foreach (var secret in new[] { "482617", "917355", code, code.Replace("-", "") })
        {
            Assert.DoesNotContain(_log, l => l.Contains(secret));
            Assert.DoesNotContain(secret, File.ReadAllText(Path.Combine(_dir, "teacher.json")));
        }
    }
}
