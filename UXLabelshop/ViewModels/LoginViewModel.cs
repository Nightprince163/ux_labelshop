using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UXLabelshop.Services;

namespace UXLabelshop.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly AuthService _auth;
    private readonly DbInitService _dbInit;
    private readonly ConfigService _config;

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _rememberUsername = true;

    [ObservableProperty]
    private bool _rememberPassword;

    /// <summary>窗口启动时用来回填 PasswordBox（密码不做绑定，避免留在可视化树里）。</summary>
    public string? SavedPassword => _config.RememberedPassword;

    public LoginResult? Result { get; private set; }

    public LoginViewModel(AuthService auth, DbInitService dbInit, ConfigService config)
    {
        _auth = auth;
        _dbInit = dbInit;
        _config = config;
        Username = _config.Current.RememberedUsername ?? "";
        RememberPassword = _config.Current.RememberLoginPassword;
    }

    /// <summary>用户取消勾选时立刻抹掉已存密码，不必等到下次登录成功。</summary>
    partial void OnRememberPasswordChanged(bool value)
    {
        if (!value) _config.SetRememberedPassword(null);
    }

    /// <summary>由窗口在登录按钮点击时传入密码（PasswordBox 不做绑定）。</summary>
    [RelayCommand]
    private void Login(string? password)
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = "请输入工号";
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            ErrorMessage = "请输入密码";
            return;
        }

        Busy = true;
        try
        {
            _dbInit.Initialize(); // 幂等：首次连接时建表/种子管理员
            Result = _auth.Login(Username.Trim(), password);

            if (Result.Success)
            {
                _config.SaveCredentials(Username.Trim(), RememberUsername, RememberPassword, password);
            }
            else
            {
                ErrorMessage = Result.Message;
                // 用旧密码反复失败时给个明确出路
                if (RememberPassword && password == _config.RememberedPassword)
                    ErrorMessage += "\n若密码已修改，请取消「记住密码」后重新输入。";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "数据库连接失败：" + ex.Message + "\n请点击「数据库设置」检查连接信息。";
        }
        finally
        {
            Busy = false;
        }
    }
}
