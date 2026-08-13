using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;

namespace GameTranslator.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new InstallerForm();
        Application.Run(form);
    }
}

internal sealed class InstallerForm : Form
{
    private readonly TextBox _folder = new();
    private readonly Button _install = new();
    private readonly Label _status = new();

    public InstallerForm()
    {
        Text = "Game Translator 安装程序";
        Width = 560;
        Height = 220;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var title = new Label { Text = "安装 Game Translator 0.1.0", AutoSize = true, Left = 24, Top = 20, Font = new Font(Font, FontStyle.Bold) };
        var prompt = new Label { Text = "选择安装目录：", AutoSize = true, Left = 24, Top = 62 };
        _folder.Left = 24;
        _folder.Top = 86;
        _folder.Width = 390;
        _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameTranslator");
        var browse = new Button { Text = "浏览…", Left = 424, Top = 84, Width = 90 };
        browse.Click += (_, _) => Browse();
        _install.Text = "安装";
        _install.Left = 424;
        _install.Top = 128;
        _install.Width = 90;
        _install.Click += async (_, _) => await InstallAsync();
        _status.Left = 24;
        _status.Top = 134;
        _status.Width = 380;
        _status.AutoEllipsis = true;

        Controls.AddRange([title, prompt, _folder, browse, _install, _status]);
        AcceptButton = _install;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _folder.Text, Description = "选择 Game Translator 安装目录" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
    }

    private async Task InstallAsync()
    {
        var target = _folder.Text.Trim();
        if (target.Length == 0) return;
        _install.Enabled = false;
        _status.Text = "正在安装…";
        try
        {
            Directory.CreateDirectory(target);
            await using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameTranslator.payload.zip")
                ?? throw new InvalidDataException("安装包缺少程序文件。");
            var temp = Path.Combine(Path.GetTempPath(), $"GameTranslator-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var output = File.Create(temp)) await payload.CopyToAsync(output);
                ZipFile.ExtractToDirectory(temp, target, true);
            }
            finally { File.Delete(temp); }

            CreateShortcut(target);
            _status.Text = "安装完成";
            if (MessageBox.Show(this, "安装完成，是否立即启动？", "Game Translator", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                Process.Start(new ProcessStartInfo(Path.Combine(target, "GameTranslator.exe")) { UseShellExecute = true });
            Close();
        }
        catch (Exception exception)
        {
            _status.Text = "安装失败";
            MessageBox.Show(this, exception.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = true;
        }
    }

    private static void CreateShortcut(string target)
    {
        var shortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Game Translator.url");
        File.WriteAllText(shortcut, $"[InternetShortcut]\nURL=file:///{Path.Combine(target, "GameTranslator.exe").Replace('\\', '/')}\nIconFile={Path.Combine(target, "GameTranslator.exe")}\nIconIndex=0\n");
    }
}
