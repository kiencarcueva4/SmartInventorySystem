namespace SmartInventorySystem.Services
{
    public static class RoleNames
    {
        public const string Administrator = "Administrator";
        public const string InventoryPersonnel = "Inventory Personnel";
        public const string SalesCashier = "Sales/Cashier";
    }

    /// <summary>Keys for SystemSettings. Defaults are seeded in DatabaseSeeder.</summary>
    public static class SettingKeys
    {
        // Movement classification
        public const string MovementEvaluationDays = "Movement.EvaluationDays";
        public const string MovementFastMinTurnover = "Movement.FastMinTurnover";
        public const string MovementSlowMaxTurnover = "Movement.SlowMaxTurnover";
        // Forecasting (weights must total 1.00)
        public const string ForecastWeightRecent = "Forecast.WeightRecent";
        public const string ForecastWeightPrevious = "Forecast.WeightPrevious";
        public const string ForecastWeightOlder = "Forecast.WeightOlder";
        public const string ForecastPeriodDays = "Forecast.PeriodDays";
        public const string ForecastAlpha = "Forecast.ExponentialAlpha";
        // Reordering
        public const string ReorderDefaultLeadTimeDays = "Reorder.DefaultLeadTimeDays";
        public const string ReorderSafetyStockDays = "Reorder.SafetyStockDays";
        public const string ReorderOverstockMultiplier = "Reorder.OverstockMultiplier";
        // Alerts
        public const string AlertCriticalStockPercent = "Alert.CriticalStockPercentOfSafety";
        public const string AlertObsolescenceNoUseDays = "Alert.ObsolescenceNoUseDays";
        public const string AlertForecastErrorPercent = "Alert.ForecastErrorWarningPercent";
    }
}
