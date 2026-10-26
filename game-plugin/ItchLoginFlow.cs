using System.Globalization;

namespace AmongLLM.GamePlugin;

internal enum LoginUiAction { Wait, SubmitBirthDate, SignIn, ConfirmSuccess, SaveFriendCode, CloseFriendCode, Complete }

internal sealed record LoginUiState(
    bool AgePrompt = false, bool SignInPrompt = false, bool SuccessPrompt = false,
    bool FriendCodePrompt = false, bool FriendCodeSaved = false,
    bool Finished = false, bool LoggedIn = false, bool HasFriendCode = false,
    bool RequiresFriendCode = false, bool RequiresParent = false, bool Failed = false);

// Decisions are independent of Unity so repeated frames, backend waits and
// prompt ordering can be tested without submitting real account requests.
internal sealed class ItchLoginFlow
{
    public const string DefaultDateOfBirth = "2000-04-04"; // Date explicitly supplied by the operator.
    private bool ageSubmitted;
    private int ageSubmissions;
    private bool signInSubmitted;
    private bool successConfirmed;
    private bool friendCodeSubmitted;
    private bool friendCodeClosed;
    public string Stage { get; private set; } = "platform login";

    // Only a definite backend rejection permits resubmission. A timeout with an
    // unknown outcome retains the flag across command retries.
    public void FriendCodeRejected() => friendCodeSubmitted = false;

    public static DateTime? ParseBirthDate(string? value)
    {
        if (value == null) return null;
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date.Year <= 1900 || date.Year >= DateTime.UtcNow.Year)
            throw new ArgumentException("dateOfBirth must be a real date in yyyy-MM-dd format within the game's supported years (1901 to last year).");
        return date;
    }

    public LoginUiAction Advance(LoginUiState state, bool hasBirthDate)
    {
        if (state.Failed)
            throw new InvalidOperationException($"The game reported a login error during {Stage}. Inspect the account dialog and game logs.");
        if (state.RequiresParent)
            throw new InvalidOperationException("Login requires guardian verification. Complete the game's guardian flow before retrying; no permissions were overridden.");

        if (!state.AgePrompt) ageSubmitted = false;
        if (state.AgePrompt)
        {
            Stage = "age verification";
            if (!hasBirthDate)
                throw new InvalidOperationException("Age verification requires dateOfBirth (the account owner's actual date, yyyy-MM-dd). Retry with the same Itch token to resume this login.");
            if (ageSubmitted) return LoginUiAction.Wait;
            if (ageSubmissions >= 3)
                throw new InvalidOperationException("The game repeatedly requested age verification after three submissions. Inspect the account dialog before retrying.");
            ageSubmitted = true;
            ageSubmissions++;
            return LoginUiAction.SubmitBirthDate;
        }
        if (state.SignInPrompt && !signInSubmitted)
        {
            Stage = "account creation";
            signInSubmitted = true;
            return LoginUiAction.SignIn;
        }
        if (state.SuccessPrompt)
        {
            Stage = "login success confirmation";
            if (successConfirmed) return LoginUiAction.Wait;
            successConfirmed = true;
            return LoginUiAction.ConfirmSuccess;
        }
        if (state.FriendCodePrompt)
        {
            Stage = "friend code registration";
            if (state.FriendCodeSaved && state.HasFriendCode)
            {
                if (friendCodeClosed) return LoginUiAction.Wait;
                friendCodeClosed = true;
                return LoginUiAction.CloseFriendCode;
            }
            if (friendCodeSubmitted) return LoginUiAction.Wait;
            friendCodeSubmitted = true;
            return LoginUiAction.SaveFriendCode;
        }
        if (state.Finished)
        {
            if (!state.LoggedIn)
                throw new InvalidOperationException("The game finished login without LoggedIn status. Inspect the account dialog and game logs.");
            if (!state.SignInPrompt && (!state.RequiresFriendCode || state.HasFriendCode))
                return LoginUiAction.Complete;
        }
        return LoginUiAction.Wait;
    }
}
