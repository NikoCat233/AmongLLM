namespace AmongLLM.GamePlugin.Tests;

public sealed class ItchLoginFlowTests
{
    [Fact]
    public void FirstAccountRunsAllPromptsAndWaitsForBackendBeforeClosingFriendCode()
    {
        var flow = new ItchLoginFlow();
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(), true));
        Assert.Equal(LoginUiAction.SubmitBirthDate, flow.Advance(new(AgePrompt: true), true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(AgePrompt: true), true));
        Assert.Equal(LoginUiAction.SignIn, flow.Advance(new(SignInPrompt: true), true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(SignInPrompt: true), true));
        Assert.Equal(LoginUiAction.ConfirmSuccess, flow.Advance(new(SuccessPrompt: true, LoggedIn: true), true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(SuccessPrompt: true, LoggedIn: true), true));
        Assert.Equal(LoginUiAction.SaveFriendCode, flow.Advance(new(FriendCodePrompt: true, LoggedIn: true), true));
        for (int i = 0; i < 100; i++)
            Assert.Equal(LoginUiAction.Wait, flow.Advance(new(FriendCodePrompt: true, LoggedIn: true), true));
        // A nonempty field alone is insufficient: the native save-success UI
        // must confirm that the backend accepted the registration.
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(FriendCodePrompt: true, LoggedIn: true, HasFriendCode: true), true));
        Assert.Equal(LoginUiAction.CloseFriendCode, flow.Advance(new(FriendCodePrompt: true, FriendCodeSaved: true, LoggedIn: true, HasFriendCode: true), true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(FriendCodePrompt: true, FriendCodeSaved: true, Finished: true, LoggedIn: true, HasFriendCode: true), true));
        Assert.Equal(LoginUiAction.Complete, flow.Advance(new(Finished: true, LoggedIn: true, RequiresFriendCode: true, HasFriendCode: true), true));
    }

    [Fact]
    public void NativeFlowFinishAndLoggedInAndFriendCodeMustAllBeReady()
    {
        var flow = new ItchLoginFlow();
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(LoggedIn: true, HasFriendCode: true), true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(Finished: true, LoggedIn: true, RequiresFriendCode: true), true));
        Assert.Equal(LoginUiAction.Complete, flow.Advance(new(Finished: true, LoggedIn: true, HasFriendCode: true, RequiresFriendCode: true), true));
        Assert.Throws<InvalidOperationException>(() => new ItchLoginFlow().Advance(new(Finished: true), true));
    }

    [Fact]
    public void AccountsWithoutFriendPermissionCanFinishWithoutFriendCode()
    {
        Assert.Equal(LoginUiAction.Complete, new ItchLoginFlow().Advance(new(Finished: true, LoggedIn: true), true));
    }

    [Fact]
    public void OnlyAnExplicitFriendCodeRejectionAllowsAnotherSubmission()
    {
        var flow = new ItchLoginFlow();
        var prompt = new LoginUiState(FriendCodePrompt: true);
        Assert.Equal(LoginUiAction.SaveFriendCode, flow.Advance(prompt, true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(prompt, true));
        flow.FriendCodeRejected();
        Assert.Equal(LoginUiAction.SaveFriendCode, flow.Advance(prompt, true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(prompt, true));
    }

    [Fact]
    public void MissingBirthDateBlocksAndCanResumeSameFlowWithoutDuplicateAccountCreation()
    {
        var flow = new ItchLoginFlow();
        var error = Assert.Throws<InvalidOperationException>(() => flow.Advance(new(AgePrompt: true), false));
        Assert.Contains("dateOfBirth", error.Message);
        Assert.Equal(LoginUiAction.SubmitBirthDate, flow.Advance(new(AgePrompt: true), true));
        Assert.Equal(LoginUiAction.SignIn, flow.Advance(new(SignInPrompt: true), true));
        Assert.Equal(LoginUiAction.Wait, flow.Advance(new(SignInPrompt: true), true));
    }

    [Fact]
    public void RepeatedAgeGateIsResubmittedOnlyAfterItClosesAndHasABound()
    {
        var flow = new ItchLoginFlow();
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(LoginUiAction.SubmitBirthDate, flow.Advance(new(AgePrompt: true), true));
            Assert.Equal(LoginUiAction.Wait, flow.Advance(new(AgePrompt: true), true));
            Assert.Equal(LoginUiAction.Wait, flow.Advance(new(), true));
        }
        Assert.Throws<InvalidOperationException>(() => flow.Advance(new(AgePrompt: true), true));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void GuardianAndGameErrorsNeverProduceSuccessOrMoreSubmissions(bool parent, bool failed)
    {
        var flow = new ItchLoginFlow();
        Assert.Throws<InvalidOperationException>(() => flow.Advance(new(
            RequiresParent: parent, Failed: failed, AgePrompt: true, SignInPrompt: true,
            FriendCodePrompt: true, Finished: true, LoggedIn: true, HasFriendCode: true), true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ResumingAfterInterruptionDoesNotResubmitNativeWrites(int stage)
    {
        var flow = new ItchLoginFlow();
        var state = stage switch
        {
            1 => new LoginUiState(SignInPrompt: true),
            2 => new LoginUiState(SuccessPrompt: true),
            _ => new LoginUiState(FriendCodePrompt: true)
        };
        Assert.NotEqual(LoginUiAction.Wait, flow.Advance(state, true));
        // Cancellation/disconnect stops advancement in ExecuteWork; a same-token
        // retry reuses this decision state rather than replaying the write.
        Assert.Equal(LoginUiAction.Wait, flow.Advance(state, true));
    }

    [Fact]
    public void DateParsingUsesOnlyTheSuppliedDateAndNeverInventsAdultStatus()
    {
        Assert.Equal(new DateTime(2000, 4, 4), ItchLoginFlow.ParseBirthDate(ItchLoginFlow.DefaultDateOfBirth));
        string recentDate = new DateTime(DateTime.UtcNow.Year - 2, 4, 4).ToString("yyyy-MM-dd");
        Assert.Equal(DateTime.UtcNow.Year - 2, ItchLoginFlow.ParseBirthDate(recentDate)!.Value.Year);
        Assert.Null(ItchLoginFlow.ParseBirthDate(null));
        foreach (var value in new[] { "", "2000-02-30", "2000-4-4", "1900-04-04", $"{DateTime.UtcNow.Year}-01-01" })
        {
            var error = Assert.Throws<ArgumentException>(() => ItchLoginFlow.ParseBirthDate(value));
            Assert.DoesNotContain(value == "" ? "unreachable" : value, error.Message);
        }
    }
}
