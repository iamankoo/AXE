using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AxeV2.Access;

public partial class AccessView : UserControl
{
    private enum AccessPage
    {
        Choose,
        Payment,
        Invitation
    }

    private readonly AccessController _controller;

    private bool _disposed;
    private bool _invitationVisible;
    private AccessPage _currentPage = AccessPage.Choose;
    private byte[]? _paymentScreenshot;
    private string? _paymentScreenshotMediaType;

    public AccessView(AccessController controller)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        InitializeComponent();

        _controller.PhaseChanged += Controller_PhaseChanged;
        _controller.Tick += Controller_Tick;

        Loaded += AccessView_Loaded;

        BrowseScreenshotButton.Click += BrowseScreenshotButton_Click;
        ContinueButton.Click += ContinueButton_Click;
        PaymentBackButton.Click += PaymentBackButton_Click;
        InvitationButton.Click += InvitationButton_Click;
        InvitationBackButton.Click += InvitationBackButton_Click;
        SubmitPaymentButton.Click += SubmitPaymentButton_Click;
        SubmitInvitationButton.Click += SubmitInvitationButton_Click;
        CancelButton.Click += CancelButton_Click;
        RetryButton.Click += RetryButton_Click;

        Plan1Hour.Checked += Plan_Checked;
        Plan5Hours.Checked += Plan_Checked;
        Plan10Hours.Checked += Plan_Checked;

        LoadQrCode();
        UpdatePaymentPlanDisplay();
        ShowChoosePage();
    }

    private async void AccessView_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AccessView_Loaded;

        try
        {
            await _controller.StartAsync();
            UpdateState();
        }
        catch (Exception ex)
        {
            ShowValidation($"Unable to initialize access: {ex.Message}");
        }
    }

    private void Controller_PhaseChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(UpdateState);
            return;
        }

        UpdateState();
    }

    private void Controller_Tick(object? sender, TimeSpan remaining)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => UpdateRemaining(remaining));
            return;
        }

        UpdateRemaining(remaining);
    }

    private void Plan_Checked(object sender, RoutedEventArgs e)
    {
        UpdatePaymentPlanDisplay();

        if (_invitationVisible)
        {
            InvitationPlanText.Text = $"{SelectedPlan().Label} — {SelectedPlan().PriceText}";
        }
    }

    private void UpdatePaymentPlanDisplay()
    {
        var plan = SelectedPlan();

        PaymentPlanText.Text = plan.Label;
        PaymentAmountText.Text = plan.PriceText;
        InvitationPlanText.Text = $"{plan.Label} — {plan.PriceText}";

        if (string.IsNullOrWhiteSpace(AmountPaidBox.Text) ||
            decimal.TryParse(AmountPaidBox.Text.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var existing) &&
            existing != plan.PriceInr)
        {
            AmountPaidBox.Text = plan.PriceInr.ToString(CultureInfo.InvariantCulture);
        }
    }

    private void UpdateState()
    {
        var phase = _controller.Phase;

        HideAllPages();
        SetBusy(false);
        ValidationText.Text = string.Empty;

        switch (phase)
        {
            case AccessPhase.NotConfigured:
                OfflinePage.Visibility = Visibility.Visible;
                OfflineTitle.Text = "Server not configured";
                OfflineMessage.Text = "This AXE v2 build is not connected to an access server.";
                RetryButton.Visibility = Visibility.Collapsed;
                StepBadge.Visibility = Visibility.Collapsed;
                PageTitle.Text = "Access configuration required";
                break;

            case AccessPhase.Checking:
                OfflinePage.Visibility = Visibility.Visible;
                OfflineTitle.Text = "Checking access";
                OfflineMessage.Text = "Verifying your current authorization...";
                RetryButton.Visibility = Visibility.Collapsed;
                StepBadge.Visibility = Visibility.Collapsed;
                PageTitle.Text = "Please wait";
                break;

            case AccessPhase.NeedsAccess:
                StepBadge.Visibility = Visibility.Visible;
                RetryButton.Visibility = Visibility.Visible;

                if (_currentPage == AccessPage.Invitation || _invitationVisible)
                {
                    ShowInvitationPage();
                }
                else if (_currentPage == AccessPage.Payment)
                {
                    ShowPaymentPage();
                }
                else
                {
                    ShowChoosePage();
                }
                break;

            case AccessPhase.Pending:
                PendingPage.Visibility = Visibility.Visible;
                StepBadge.Visibility = Visibility.Collapsed;
                PageTitle.Text = "Request submitted";
                break;

            case AccessPhase.Active:
                StepBadge.Visibility = Visibility.Collapsed;
                PageTitle.Text = "Access active";
                UpdateRemaining(_controller.Remaining);
                break;

            case AccessPhase.Offline:
                OfflinePage.Visibility = Visibility.Visible;
                OfflineTitle.Text = "Connection unavailable";
                OfflineMessage.Text = _controller.Message ??
                                      "AXE could not verify access with the server.";
                RetryButton.Visibility = Visibility.Visible;
                StepBadge.Visibility = Visibility.Collapsed;
                PageTitle.Text = "Unable to verify access";
                break;
        }
    }

    private void HideAllPages()
    {
        ChoosePage.Visibility = Visibility.Collapsed;
        PaymentPage.Visibility = Visibility.Collapsed;
        InvitationPage.Visibility = Visibility.Collapsed;
        PendingPage.Visibility = Visibility.Collapsed;
        OfflinePage.Visibility = Visibility.Collapsed;
    }

    private void ShowChoosePage()
    {
        _currentPage = AccessPage.Choose;
        _invitationVisible = false;
        HideAllPages();
        ChoosePage.Visibility = Visibility.Visible;
        StepBadge.Visibility = Visibility.Visible;
        StepText.Text = "Step 1 of 2";
        PageTitle.Text = "Get access to AXE";
        ValidationText.Text = string.Empty;
    }

    private void ShowPaymentPage()
    {
        _currentPage = AccessPage.Payment;
        _invitationVisible = false;
        HideAllPages();
        PaymentPage.Visibility = Visibility.Visible;
        StepBadge.Visibility = Visibility.Visible;
        StepText.Text = "Step 2 of 2";
        PageTitle.Text = "Complete payment";
        UpdatePaymentPlanDisplay();
    }

    private void ShowInvitationPage()
    {
        _currentPage = AccessPage.Invitation;
        _invitationVisible = true;
        HideAllPages();
        InvitationPage.Visibility = Visibility.Visible;
        StepBadge.Visibility = Visibility.Collapsed;
        PageTitle.Text = "Invitation access";
        InvitationNameBox.Text = NameBox.Text;
        InvitationPlanText.Text = $"{SelectedPlan().Label} — {SelectedPlan().PriceText}";
        ValidationText.Text = string.Empty;
    }

    private void ShowValidation(string message)
    {
        ValidationText.Text = message;
    }

    private void UpdateRemaining(TimeSpan remaining)
    {
        if (_controller.Phase != AccessPhase.Active)
        {
            return;
        }

        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        PageTitle.Text = $"Access active • {remaining:hh\\:mm\\:ss} remaining";
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NormalizeName(NameBox.Text);

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation("Please enter your name before continuing.");
            NameBox.Focus();
            return;
        }

        ShowPaymentPage();
    }

    private void PaymentBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChoosePage();
        NameBox.Focus();
    }

    private void InvitationButton_Click(object sender, RoutedEventArgs e)
    {
        ShowInvitationPage();
        InvitationNameBox.Focus();
    }

    private void InvitationBackButton_Click(object sender, RoutedEventArgs e)
    {
        ShowChoosePage();
        NameBox.Focus();
    }

    private void LoadQrCode()
    {
        try
        {
            var uri = new Uri(
                "pack://application:,,,/Assets/QR.jpeg",
                UriKind.Absolute);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = uri;
            bitmap.EndInit();
            bitmap.Freeze();

            QrCodeImage.Source = bitmap;
            QrCodeStatusText.Text = "Scan the QR code to pay the selected amount.";
            QrCodeStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }
        catch (Exception ex)
        {
            QrCodeImage.Source = null;
            QrCodeStatusText.Text = "Payment QR could not be loaded from the application assets.";
            QrCodeStatusText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#FFE45B65")!);
            ShowValidation($"QR asset could not be loaded: {ex.Message}");
        }
    }

    private AccessPlan SelectedPlan()
    {
        if (Plan10Hours.IsChecked == true)
        {
            return AccessPlan.Find("10h")!;
        }

        if (Plan5Hours.IsChecked == true)
        {
            return AccessPlan.Find("5h")!;
        }

        return AccessPlan.Find("1h")!;
    }

    private static string NormalizeName(string? value) =>
        value?.Trim() ?? string.Empty;

    private static bool TryParseAmount(string? value, out decimal amount) =>
        decimal.TryParse(
            value?.Trim(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out amount);

    private void BrowseScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select payment screenshot",
            Filter = "Payment screenshots (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();

            var mediaType = extension switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                _ => null
            };

            if (mediaType is null)
            {
                ShowValidation("Please select a JPG, JPEG, or PNG screenshot.");
                return;
            }

            var fileInfo = new FileInfo(dialog.FileName);
            const long maxScreenshotBytes = 10 * 1024 * 1024;

            if (fileInfo.Length <= 0)
            {
                ShowValidation("The selected screenshot is empty.");
                return;
            }

            if (fileInfo.Length > maxScreenshotBytes)
            {
                ShowValidation("The payment screenshot must be 10 MB or smaller.");
                return;
            }

            _paymentScreenshot = File.ReadAllBytes(dialog.FileName);
            _paymentScreenshotMediaType = mediaType;
            ScreenshotPathBox.Text = Path.GetFileName(dialog.FileName);
            ShowValidation("Payment screenshot selected.");
        }
        catch (IOException ex)
        {
            ClearScreenshot();
            ShowValidation($"Unable to read the screenshot: {ex.Message}");
        }
        catch (UnauthorizedAccessException)
        {
            ClearScreenshot();
            ShowValidation("Windows denied access to the selected screenshot.");
        }
    }

    private async void SubmitPaymentButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NormalizeName(NameBox.Text);

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation("Please enter your name.");
            PaymentBackButton.Focus();
            return;
        }

        var plan = SelectedPlan();

        if (!TryParseAmount(AmountPaidBox.Text, out var amountPaid))
        {
            ShowValidation("Enter a valid payment amount.");
            AmountPaidBox.Focus();
            return;
        }

        if (amountPaid != plan.PriceInr)
        {
            ShowValidation($"The payment amount must be exactly {plan.PriceText} for the selected plan.");
            AmountPaidBox.Focus();
            return;
        }

        var reference = ReferenceBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(reference))
        {
            ShowValidation("Please enter the UTR or transaction reference.");
            ReferenceBox.Focus();
            return;
        }

        if (reference.Length > 128)
        {
            ShowValidation("The transaction reference is too long.");
            ReferenceBox.Focus();
            return;
        }

        if (_paymentScreenshot is null ||
            string.IsNullOrWhiteSpace(_paymentScreenshotMediaType))
        {
            ShowValidation("Please select your payment screenshot.");
            return;
        }

        var screenshot = _paymentScreenshot;
        var mediaType = _paymentScreenshotMediaType;

        SetBusy(true);

        try
        {
            await _controller.SubmitPaymentAsync(
                name,
                plan,
                amountPaid,
                reference,
                screenshot,
                mediaType,
                CancellationToken.None);

            ClearScreenshot();
            ReferenceBox.Clear();
            AmountPaidBox.Text = plan.PriceInr.ToString(CultureInfo.InvariantCulture);
            UpdateState();
        }
        catch (AccessException ex)
        {
            ShowValidation(ex.Message);
            UpdateState();
        }
        catch (Exception ex)
        {
            ShowValidation($"Unable to submit the payment request: {ex.Message}");
            UpdateState();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SubmitInvitationButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NormalizeName(InvitationNameBox.Text);
        var code = InvitationCodeBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation("Please enter your name.");
            InvitationNameBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            ShowValidation("Please enter an invitation code.");
            InvitationCodeBox.Focus();
            return;
        }

        NameBox.Text = name;
        var plan = SelectedPlan();

        SetBusy(true);

        try
        {
            await _controller.SubmitInvitationAsync(
                name,
                plan,
                code,
                CancellationToken.None);

            UpdateState();
        }
        catch (AccessException ex)
        {
            ShowValidation(ex.Message);
            UpdateState();
        }
        catch (Exception ex)
        {
            ShowValidation($"Unable to submit the invitation request: {ex.Message}");
            UpdateState();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);

        try
        {
            await _controller.CancelPendingAsync();
            ClearScreenshot();
            ShowChoosePage();
            UpdateState();
        }
        catch (Exception ex)
        {
            ShowValidation($"Unable to cancel the request: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);

        try
        {
            await _controller.RetryAsync();
            UpdateState();
        }
        catch (Exception ex)
        {
            ShowValidation($"Unable to retry: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        NameBox.IsEnabled = !busy;
        InvitationNameBox.IsEnabled = !busy;
        InvitationCodeBox.IsEnabled = !busy;

        Plan1Hour.IsEnabled = !busy;
        Plan5Hours.IsEnabled = !busy;
        Plan10Hours.IsEnabled = !busy;

        ContinueButton.IsEnabled = !busy;
        PaymentBackButton.IsEnabled = !busy;
        InvitationButton.IsEnabled = !busy;
        InvitationBackButton.IsEnabled = !busy;
        BrowseScreenshotButton.IsEnabled = !busy;
        SubmitPaymentButton.IsEnabled = !busy;
        SubmitInvitationButton.IsEnabled = !busy;

        AmountPaidBox.IsEnabled = !busy;
        ReferenceBox.IsEnabled = !busy;

        CancelButton.IsEnabled =
            !busy && _controller.Phase == AccessPhase.Pending;

        RetryButton.IsEnabled =
            !busy && _controller.Phase == AccessPhase.Offline;
    }

    private void ClearScreenshot()
    {
        if (_paymentScreenshot is not null)
        {
            Array.Clear(_paymentScreenshot, 0, _paymentScreenshot.Length);
        }

        _paymentScreenshot = null;
        _paymentScreenshotMediaType = null;
        ScreenshotPathBox.Text = "No screenshot selected";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _controller.PhaseChanged -= Controller_PhaseChanged;
        _controller.Tick -= Controller_Tick;

        ClearScreenshot();
    }
}
