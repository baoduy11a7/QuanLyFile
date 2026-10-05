using System;
using System.Collections.Generic;

namespace InvoiceManager.Services.Providers.Common
{
    public static class DateRangeSplitter
    {
        /// <summary>
        /// Chia nhỏ khoảng ngày thành từng chu kỳ nhỏ hơn (mặc định tối đa 31 ngày)
        /// để tránh vượt quá giới hạn truy vấn của Cổng Tổng cục Thuế.
        /// </summary>
        public static IEnumerable<(DateTime Start, DateTime End)> Split(DateTime fromDate, DateTime toDate, int maxDays = 31)
        {
            if (fromDate > toDate)
            {
                var temp = fromDate;
                fromDate = toDate;
                toDate = temp;
            }

            var currentStart = fromDate.Date;
            var finalEnd = toDate.Date;

            while (currentStart <= finalEnd)
            {
                var currentEnd = currentStart.AddDays(maxDays - 1);
                if (currentEnd > finalEnd)
                {
                    currentEnd = finalEnd;
                }

                yield return (currentStart, currentEnd);

                currentStart = currentEnd.AddDays(1);
            }
        }
    }
}
