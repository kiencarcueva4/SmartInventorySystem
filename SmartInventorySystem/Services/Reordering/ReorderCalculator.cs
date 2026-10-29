using System;
using SmartInventorySystem.Domain.Enums;
using SmartInventorySystem.Services.Core;

namespace SmartInventorySystem.Services.Reordering
{
    public sealed record ReorderResult(UrgencyLevel Urgency, decimal RecommendedQuantity, decimal LeadTimeDemand,
        decimal TargetLevel, string Reason);

    /// <summary>
    /// The reorder rules, in one place so they are easy to explain, test and change.
    ///
    ///   d = forecasted daily demand     L = supplier lead time (days)
    ///   S = safety stock                R = reorder point          Q = current stock
    ///
    ///   Lead-time demand    = d x L
    ///   Target level        = lead-time demand + S
    ///   Recommended quantity = max(0, target level - Q)
    ///                          (when a reorder is needed and the result is below the item's
    ///                           minimum order quantity, the minimum order quantity is used;
    ///                           the result is rounded up to a whole unit)
    ///
    ///   URGENT             : Q &lt;= 0, or Q &lt;= S, or Q &lt; lead-time demand
    ///                        (stock runs out before a new delivery could arrive)
    ///   RECOMMENDED        : Q &lt;= R, or Q &lt; target level
    ///   NOT YET NECESSARY  : otherwise
    /// </summary>
    public static class ReorderCalculator
    {
        public static ReorderResult Evaluate(decimal stock, decimal safetyStock, decimal reorderPoint,
            decimal dailyDemand, int leadTimeDays, decimal minimumOrderQuantity, string unit)
        {
            var leadTimeDemand = dailyDemand * leadTimeDays;
            var target = leadTimeDemand + safetyStock;

            UrgencyLevel urgency;
            string why;

            if (stock <= 0)
            {
                urgency = UrgencyLevel.Urgent;
                why = "The item is out of stock.";
            }
            else if (stock <= safetyStock)
            {
                urgency = UrgencyLevel.Urgent;
                why = $"Stock ({Fmt.Qty(stock)} {unit}) is at or below the safety stock ({Fmt.Qty(safetyStock)}).";
            }
            else if (stock < leadTimeDemand)
            {
                urgency = UrgencyLevel.Urgent;
                why = $"Stock ({Fmt.Qty(stock)} {unit}) is lower than the demand expected during the {leadTimeDays}-day lead time ({Fmt.Qty(leadTimeDemand)}). It would run out before a new delivery arrives.";
            }
            else if (stock <= reorderPoint)
            {
                urgency = UrgencyLevel.Recommended;
                why = $"Stock ({Fmt.Qty(stock)} {unit}) is at or below the reorder point ({Fmt.Qty(reorderPoint)}).";
            }
            else if (stock < target)
            {
                urgency = UrgencyLevel.Recommended;
                why = $"Stock ({Fmt.Qty(stock)} {unit}) is below the target level ({Fmt.Qty(target)}), which is the lead-time demand plus safety stock.";
            }
            else
            {
                return new ReorderResult(UrgencyLevel.NotYetNecessary, 0m, leadTimeDemand, target,
                    $"Stock ({Fmt.Qty(stock)} {unit}) is above the reorder point ({Fmt.Qty(reorderPoint)}) and the target level ({Fmt.Qty(target)}).");
            }

            var needed = Math.Max(0m, target - stock);
            var quantity = Math.Ceiling(needed);
            var detail = $" Suggested order = target level {Fmt.Qty(target)} - stock {Fmt.Qty(stock)} = {Fmt.Qty(quantity)} {unit}.";

            if (quantity < minimumOrderQuantity)
            {
                quantity = minimumOrderQuantity;
                detail += $" Raised to the item's minimum order quantity ({Fmt.Qty(minimumOrderQuantity)} {unit}).";
            }

            return new ReorderResult(urgency, quantity, leadTimeDemand, target, why + detail);
        }
    }
}
