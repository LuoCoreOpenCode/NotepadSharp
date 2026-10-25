using System.Windows;
using System.Windows.Controls;
using Application = System.Windows.Application;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace NotepadSharp.Controls;

/// <summary>
/// 简易输入对话框
/// </summary>
public static class Prompt
{
    public static string? ShowInputDialog(Window owner, string title, string message, string defaultValue = "")
    {
        string? result = defaultValue;

        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Owner = owner,
            Background = Application.Current.Resources["PanelBackground"] as System.Windows.Media.Brush,
        };

        var inputBox = new TextBox
        {
            Margin = new Thickness(12, 4, 12, 4),
            Padding = new Thickness(4, 2, 4, 2),
            Text = defaultValue,
        };

        var okButton = new Button
        {
            Content = "确定",
            Width = 70,
            Margin = new Thickness(4),
            IsDefault = true,
        };

        var cancelButton = new Button
        {
            Content = "取消",
            Width = 70,
            Margin = new Thickness(4),
            IsCancel = true,
        };

        okButton.Click += (_, _) =>
        {
            result = inputBox.Text;
            dialog.DialogResult = true;
        };

        inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                result = inputBox.Text;
                dialog.DialogResult = true;
                e.Handled = true;
            }
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = message,
            Margin = new Thickness(12, 12, 12, 4),
            Foreground = Application.Current.Resources["TextBrush"] as System.Windows.Media.Brush,
        });
        stack.Children.Add(inputBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 12, 8),
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        stack.Children.Add(buttons);

        dialog.Content = stack;
        inputBox.Focus();

        if (dialog.ShowDialog() == true)
            return result;
        return null;
    }
}
