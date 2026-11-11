using System.Windows;
using UXLabelshop.Services;

namespace UXLabelshop.Views;

public partial class DatabaseSettingsWindow : Window
{
    private readonly ConfigService _config;
    private bool _passwordEntered;

    public DatabaseSettingsWindow(ConfigService config)
    {
        InitializeComponent();
        _config = config;

        var db = config.Current.Db;
        HostBox.Text = db.Host;
        PortBox.Text = db.Port.ToString();
        DbBox.Text = db.Database;
        UserBox.Text = db.User;
        // 密码已加密存储，不回显；留空表示不修改
        _passwordEntered = !string.IsNullOrEmpty(config.Password);
        UpdateUrlBox.Text = config.Current.UpdateUrl ?? "";
    }

    private void Test_Click(object sender, RoutedEventArgs e)
    {
        if (!Apply())
        {
            ResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }
        if (_config.TestConnection(out var error))
        {
            ResultText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            ResultText.Text = $"连接成功（{DateTime.Now:HH:mm:ss}）";
        }
        else
        {
            ResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            ResultText.Text = "连接失败：" + error;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Apply())
        {
            ResultText.Foreground = System.Windows.Media.Brushes.Firebrick;
            return;
        }
        _config.Save();
        // 连接参数变了：公式里的 DBLOOKUP 也要换连接、清掉旧结果缓存
        if (App.Services.GetService(typeof(UXLabelshop.Services.SqlLookupService))
            is UXLabelshop.Services.SqlLookupService lookup)
            lookup.Reset();
        DialogResult = true;
    }

    /// <summary>把界面值写回配置对象（不落盘）。密码留空则沿用已存密码。</summary>
    private bool Apply()
    {
        var db = _config.Current.Db;
        db.Host = HostBox.Text.Trim();
        db.Database = DbBox.Text.Trim();
        db.User = UserBox.Text.Trim();
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ResultText.Text = "端口无效";
            return false;
        }
        db.Port = port;

        var url = UpdateUrlBox.Text.Trim();
        _config.Current.UpdateUrl = url.Length == 0 ? null : url;

        if (PwdBox.Password.Length > 0)
        {
            _config.SetPassword(PwdBox.Password);
            _passwordEntered = true;
        }
        else if (!_passwordEntered)
        {
            ResultText.Text = "请输入数据库密码";
            return false;
        }
        return true;
    }
}
