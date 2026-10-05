using InvoiceManager.Services.Providers.Common;
using System;
using System.Linq;
using Xunit;

namespace InvoiceManager.Tests
{
    public class DateRangeSplitterTests
    {
        [Fact]
        public void Split_WhenRangeIsWithinMaxDays_ReturnsSingleChunk()
        {
            var from = new DateTime(2026, 1, 1);
            var to = new DateTime(2026, 1, 20);

            var chunks = DateRangeSplitter.Split(from, to, 31).ToList();

            Assert.Single(chunks);
            Assert.Equal(from, chunks[0].Start);
            Assert.Equal(to, chunks[0].End);
        }

        [Fact]
        public void Split_WhenRangeIs90Days_SplitsIntoMultipleChunksOfMax31Days()
        {
            var from = new DateTime(2026, 1, 1);
            var to = new DateTime(2026, 3, 31); // 90 ngày

            var chunks = DateRangeSplitter.Split(from, to, 31).ToList();

            Assert.True(chunks.Count >= 3);
            Assert.Equal(from, chunks.First().Start);
            Assert.Equal(to, chunks.Last().End);

            // Kiểm tra tính liên tục và không có ngày nào vượt quá 31 ngày
            for (int i = 0; i < chunks.Count; i++)
            {
                var duration = (chunks[i].End - chunks[i].Start).TotalDays + 1;
                Assert.True(duration <= 31, $"Chunk {i} có độ dài {duration} ngày, vượt quá 31 ngày.");

                if (i > 0)
                {
                    Assert.Equal(chunks[i - 1].End.AddDays(1), chunks[i].Start);
                }
            }
        }

        [Fact]
        public void Split_WhenFromIsGreaterThanTo_SwapsDatesGracefully()
        {
            var from = new DateTime(2026, 5, 20);
            var to = new DateTime(2026, 5, 1);

            var chunks = DateRangeSplitter.Split(from, to, 31).ToList();

            Assert.Single(chunks);
            Assert.Equal(to, chunks[0].Start);
            Assert.Equal(from, chunks[0].End);
        }
    }
}
