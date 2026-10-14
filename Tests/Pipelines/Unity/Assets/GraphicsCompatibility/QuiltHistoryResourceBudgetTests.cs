using System;
using System.Globalization;
using System.Threading;
using NUnit.Framework;
using ViewWeave;
using UnityEngine;

namespace ViewWeave.GraphicsCompatibility
{
    public sealed class QuiltHistoryResourceBudgetTests
    {
        [Test]
        public void GraphicsGrowthAtBudgetEqualityIsAcceptedAndOneByteAboveThrows()
        {
            long graphicsBytes = 10000;
            var budget = new QuiltHistoryResourceBudget(() => graphicsBytes, () => 0);
            budget.CaptureBaseline();

            graphicsBytes = 11024;
            Assert.DoesNotThrow(() => budget.Observe(1024, false, "URP"));
            Assert.AreEqual(1024, budget.GraphicsGrowthBytes);

            graphicsBytes = 11025;
            InsufficientMemoryException error = Assert.Throws<InsufficientMemoryException>(
                () => budget.Observe(1024, false, "URP"));
            StringAssert.StartsWith("URP History Resource Budget exceeded: observed graphics growth 0 MB; estimated texture growth 0 MB; budget 0 MB;", error.Message);
        }

        [Test]
        public void GraphicsHighWatermarkSurvivesCounterDecrease()
        {
            long graphicsBytes = 4000;
            var budget = new QuiltHistoryResourceBudget(() => graphicsBytes, () => 0);
            budget.CaptureBaseline();

            graphicsBytes = 5500;
            budget.Observe(2000, false, "URP");
            graphicsBytes = 4300;
            budget.Observe(2000, false, "URP");

            Assert.AreEqual(1500, budget.GraphicsGrowthBytes);
        }

        [Test]
        public void TextureEstimateStillGuardsWhenGraphicsCounterIsUnavailable()
        {
            long textureGrowth = 2048;
            var budget = new QuiltHistoryResourceBudget(() => 0, () => textureGrowth);
            budget.CaptureBaseline();

            InsufficientMemoryException error = Assert.Throws<InsufficientMemoryException>(
                () => budget.Observe(1024, true, "URP"));

            Assert.AreEqual(0, budget.GraphicsGrowthBytes);
            Assert.AreEqual(2048, budget.TextureGrowthBytes);
            StringAssert.Contains("graphics baseline raw bytes=0", error.Message);
            StringAssert.Contains("current graphics sample raw bytes=0", error.Message);
            StringAssert.Contains("peak estimated texture growth raw bytes=2048", error.Message);
            StringAssert.Contains("textures sampled this call=true", error.Message);
        }

        [Test]
        public void ResetClearsBaselineAndHighWatermarks()
        {
            long graphicsBytes = 1000;
            long textureGrowth = 2048;
            var budget = new QuiltHistoryResourceBudget(() => graphicsBytes, () => textureGrowth);
            budget.CaptureBaseline();

            InsufficientMemoryException firstError = Assert.Throws<InsufficientMemoryException>(
                () => budget.Observe(1024, true, "URP"));
            StringAssert.Contains("textures sampled this call=true", firstError.Message);

            graphicsBytes = 4000;
            textureGrowth = 0;
            budget.CaptureBaseline();
            graphicsBytes = 5024;
            Assert.DoesNotThrow(() => budget.Observe(1024, true, "URP"));
            Assert.AreEqual(1024, budget.GraphicsGrowthBytes);
            Assert.AreEqual(0, budget.TextureGrowthBytes);
        }

        [Test]
        public void FailureDiagnosticsKeepRawSamplesAndEditorContextAsciiInvariant()
        {
            long graphicsBytes = 10000;
            var budget = new QuiltHistoryResourceBudget(() => graphicsBytes, () => 7);
            budget.CaptureBaseline();
            graphicsBytes = 12000;

            CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                InsufficientMemoryException error = Assert.Throws<InsufficientMemoryException>(
                    () => budget.Observe(1000, false, "URP"));
                StringAssert.Contains("graphics baseline raw bytes=10000", error.Message);
                StringAssert.Contains("previous graphics sample raw bytes=10000", error.Message);
                StringAssert.Contains("current graphics sample raw bytes=12000", error.Message);
                StringAssert.Contains("peak graphics growth raw bytes=2000", error.Message);
                StringAssert.Contains("last estimated texture growth raw bytes=0", error.Message);
                StringAssert.Contains("peak estimated texture growth raw bytes=0", error.Message);
                StringAssert.Contains("textures sampled this call=false", error.Message);
                StringAssert.Contains("frame=", error.Message);
                StringAssert.Contains("mode=" + (Application.isPlaying ? "Play" : "Editor"), error.Message);
                Assert.IsTrue(IsAscii(error.Message), "Budget diagnostic must remain ASCII.");
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previousCulture;
            }
        }

        static bool IsAscii(string value)
        {
            for (int i = 0; i < value.Length; i++)
                if (value[i] > 0x7f) return false;
            return true;
        }
    }
}
