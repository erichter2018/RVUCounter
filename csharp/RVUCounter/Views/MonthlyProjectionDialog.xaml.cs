using System.Windows;
using RVUCounter.Core;
using RVUCounter.Data;

namespace RVUCounter.Views;

public partial class MonthlyProjectionDialog : Window
{
    private readonly DataManager _dataManager;
    private int _projectionShifts;
    private int _projectionExtraHours;

    public MonthlyProjectionDialog(DataManager dataManager)
    {
        InitializeComponent();
        ThemeManager.ApplyCurrentThemeTitleBar(this);

        _dataManager = dataManager;
        CheckAndResetMonthlyProjections();

        _projectionShifts = _dataManager.Settings.ProjectionDays;
        _projectionExtraHours = _dataManager.Settings.ProjectionExtraHours;
        RefreshValues();
    }

    private void CheckAndResetMonthlyProjections()
    {
        var currentMonth = DateTime.Now.ToString("yyyy-MM");
        if (_dataManager.Settings.LastProjectionMonth != currentMonth)
        {
            _dataManager.Settings.ProjectionDays = 14;
            _dataManager.Settings.ProjectionExtraHours = 0;
            _dataManager.Settings.LastProjectionMonth = currentMonth;
            _dataManager.SaveSettings();
        }
    }

    private void RefreshValues()
    {
        ShiftsValueText.Text = _projectionShifts.ToString();
        HoursValueText.Text = _projectionExtraHours.ToString();
    }

    private void ShiftsMinus_Click(object sender, RoutedEventArgs e)
    {
        if (_projectionShifts > 0) _projectionShifts--;
        RefreshValues();
    }

    private void ShiftsPlus_Click(object sender, RoutedEventArgs e)
    {
        if (_projectionShifts < 31) _projectionShifts++;
        RefreshValues();
    }

    private void HoursMinus_Click(object sender, RoutedEventArgs e)
    {
        if (_projectionExtraHours > 0) _projectionExtraHours--;
        RefreshValues();
    }

    private void HoursPlus_Click(object sender, RoutedEventArgs e)
    {
        if (_projectionExtraHours < 200) _projectionExtraHours++;
        RefreshValues();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _dataManager.Settings.ProjectionDays = _projectionShifts;
        _dataManager.Settings.ProjectionExtraHours = _projectionExtraHours;
        _dataManager.Settings.LastProjectionMonth = DateTime.Now.ToString("yyyy-MM");
        _dataManager.SaveSettings();

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
