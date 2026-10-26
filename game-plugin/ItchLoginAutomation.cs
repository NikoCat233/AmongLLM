using System.Globalization;
using AmongUs.Data;
using Assets.InnerNet;
using UnityEngine;

namespace AmongLLM.GamePlugin;

// Only advanced by the owning MCP coroutine. No global patches or background
// clicks survive command cancellation, disconnect, timeout or completion.
internal sealed class ItchLoginAutomation
{
    private readonly EOSManager eos;
    private readonly ItchLoginFlow flow = new();
    private DateTime? dateOfBirth;
    private bool friendCodeRejected;
    private int friendCodeAttempts;
    private bool friendCodeOutcomeUnknown;
    public string Stage => flow.Stage;

    public ItchLoginAutomation(EOSManager eos, DateTime? dateOfBirth)
    {
        this.eos = eos;
        this.dateOfBirth = dateOfBirth;
    }

    public bool CanResume(EOSManager current, string token) => eos && current && eos.Pointer == current.Pointer
        && string.Equals(eos.platformAuthToken, token, StringComparison.Ordinal);

    public void SetBirthDate(DateTime? value)
    {
        dateOfBirth = value;
        if (friendCodeRejected) friendCodeAttempts = 0;
    }

    public void PrepareNewLogin()
    {
        if (!DestroyableSingleton<AccountManager>.InstanceExists) return;
        var account = DestroyableSingleton<AccountManager>.Instance;
        // Old result dialogs may still be open after the previous login ended.
        // Hide them without invoking their callbacks into the old account flow.
        if (Visible(account.genericInfoDisplayBox)) account.genericInfoDisplayBox.gameObject.SetActive(false);
        if (Visible(account.signInScreen)) account.signInScreen.OnClose();
        if (Visible(eos.editAccountUsername)) eos.editAccountUsername.Close();
    }

    private static bool Visible(Component? component) => component != null && component && component.gameObject.activeInHierarchy;
    private static bool Visible(GameObject? gameObject) => gameObject != null && gameObject && gameObject.activeInHierarchy;

    public bool Advance()
    {
        if (!eos) throw new InvalidOperationException("The EOS manager was destroyed during login.");
        if (friendCodeOutcomeUnknown)
            throw new InvalidOperationException("Friend-code registration returned no authoritative response; its outcome is unknown. Inspect account status and game logs before submitting another request.");
        if (friendCodeRejected)
        {
            if (friendCodeAttempts >= 3)
                throw new InvalidOperationException("The server rejected friend-code registration three times. Inspect game logs before retrying the same token.");
            friendCodeRejected = false;
            flow.FriendCodeRejected();
        }
        if (!DestroyableSingleton<AccountManager>.InstanceExists) return false;
        var account = DestroyableSingleton<AccountManager>.Instance;
        var age = account.enterDateOfBirthScreen;
        var signIn = account.signInScreen;
        var info = account.genericInfoDisplayBox;
        var friend = eos.editAccountUsername;
        var translations = DestroyableSingleton<TranslationController>.Instance;
        bool infoVisible = Visible(info);
        // Exact owned box and localized game strings; never click arbitrary dialogs.
        bool success = infoVisible && info.titleTexxt.text == translations.GetString(StringNames.Success)
            && info.bodyText.text == translations.GetString(StringNames.SuccessLogIn);
        bool failed = infoVisible && (info.titleTexxt.text == translations.GetString(StringNames.Failed)
            || info.titleTexxt.text == translations.GetString(StringNames.SignInIssueTitle, Constants.GetCurrentPlatformName()));
        bool loggedIn = DataManager.Player.Account.LoginStatus == EOSManager.AccountLoginStatus.LoggedIn;
        bool finished = eos.HasFinishedLoginFlow();
        bool parent = Visible(account.enterGuardianEmailWindow) || Visible(account.updateGuardianEmailWindow)
            || (finished && DataManager.Player.Account.LoginStatus == EOSManager.AccountLoginStatus.WaitingForParent);
        var state = new LoginUiState(
            AgePrompt: Visible(age), SignInPrompt: Visible(signIn), SuccessPrompt: success,
            FriendCodePrompt: Visible(friend), FriendCodeSaved: friend && Visible(friend.SuccessMessage),
            Finished: finished, LoggedIn: loggedIn, HasFriendCode: !string.IsNullOrWhiteSpace(eos.FriendCode),
            RequiresFriendCode: eos.IsFriendsListAllowed(), RequiresParent: parent, Failed: failed);
        switch (flow.Advance(state, dateOfBirth.HasValue))
        {
            case LoginUiAction.SubmitBirthDate:
                var date = dateOfBirth!.Value;
                age.Year = date.Year;
                age.Month = date.Month;
                age.Day = date.Day;
                age.yearText.text = date.Year.ToString(CultureInfo.InvariantCulture);
                age.monthText.text = translations.GetMonthStringViaNumber(date.Month);
                age.dayText.text = date.Day.ToString(CultureInfo.InvariantCulture);
                // Keep the native date validation, save and transition behavior.
                age.GoToVerify();
                if (age.ShakeIfInvalid()) throw new InvalidOperationException("The game's age screen rejected dateOfBirth.");
                age.Close();
                break;
            case LoginUiAction.SignIn:
                signIn.signInButton.OnClick.Invoke();
                break;
            case LoginUiAction.ConfirmSuccess:
                info.button1.OnClick.Invoke();
                break;
            case LoginUiAction.SaveFriendCode:
                // Use the game's legal random-name generator and backend callback.
                friend.RandomizeName();
                friendCodeAttempts++;
                // SaveUsername ignores failed responses. Use the same native
                // backend call and success updates, but also observe rejection.
                string accountId = eos.userId?.ToString() ?? "";
                DestroyableSingleton<FriendsListManager>.Instance.SetFriendCode(friend.UsernameText.text,
                    (Il2CppSystem.Action<ResponseState, Response<ResponseFriendCode>>)((responseState, response) =>
                    {
                        if (!eos || !friend || (eos.userId?.ToString() ?? "") != accountId) return;
                        if (responseState != ResponseState.Success || response?.Data?.Attributes == null)
                        {
                            // Failed also includes transport errors. Only a
                            // structured backend rejection permits a new write.
                            if (responseState != ResponseState.Success && response?.Errors is { Length: > 0 })
                                friendCodeRejected = true;
                            else
                                friendCodeOutcomeUnknown = true;
                            return;
                        }
                        friend.PopUp.SetActive(false);
                        friend.SuccessMessage.SetActive(true);
                        eos.FriendCode = response.Data.Attributes.Username + "#" + response.Data.Attributes.Discriminator;
                        DataManager.Settings.Multiplayer.AllowFriendInvites = true;
                        DataManager.Settings.Save();
                    }));
                break;
            case LoginUiAction.CloseFriendCode:
                friend.Close();
                break;
            case LoginUiAction.Complete:
                return true;
        }
        return false;
    }
}
