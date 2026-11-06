using NUnit.Framework;
using UnityEngine;
using Mahjong.Unity.UI;

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class DesignViewportTests
    {
        [Test]
        public void Scale_1280x720_IsExactOne()
        {
            float scale = DesignViewport.CalculateScale(1280f, 720f);
            Assert.AreEqual(1.0f, scale, 0.001f);

            Rect content = DesignViewport.CalculateContentRect(1280f, 720f, scale);
            Assert.AreEqual(0f, content.x, 0.001f);
            Assert.AreEqual(0f, content.y, 0.001f);
            Assert.AreEqual(1280f, content.width, 0.001f);
            Assert.AreEqual(720f, content.height, 0.001f);
        }

        [Test]
        public void Scale_1920x1080_IsOnePointFive()
        {
            float scale = DesignViewport.CalculateScale(1920f, 1080f);
            Assert.AreEqual(1.5f, scale, 0.001f);

            Rect content = DesignViewport.CalculateContentRect(1920f, 1080f, scale);
            Assert.AreEqual(0f, content.x, 0.001f);
            Assert.AreEqual(0f, content.y, 0.001f);
            Assert.AreEqual(1920f, content.width, 0.001f);
            Assert.AreEqual(1080f, content.height, 0.001f);
        }

        [Test]
        public void Scale_1024x768_4to3_HasLetterbox()
        {
            // 1024/1280 = 0.8, 768/720 = 1.0667 -> min is 0.8
            float scale = DesignViewport.CalculateScale(1024f, 768f);
            Assert.AreEqual(0.8f, scale, 0.001f);

            Rect content = DesignViewport.CalculateContentRect(1024f, 768f, scale);
            // contentW = 1024, contentH = 576
            // contentX = 0, contentY = (768 - 576)/2 = 96
            Assert.AreEqual(0f, content.x, 0.001f);
            Assert.AreEqual(96f, content.y, 0.001f);
            Assert.AreEqual(1024f, content.width, 0.001f);
            Assert.AreEqual(576f, content.height, 0.001f);
        }

        [Test]
        public void Scale_2560x1080_21to9_HasPillarbox()
        {
            // 2560/1280 = 2.0, 1080/720 = 1.5 -> min is 1.5
            float scale = DesignViewport.CalculateScale(2560f, 1080f);
            Assert.AreEqual(1.5f, scale, 0.001f);

            Rect content = DesignViewport.CalculateContentRect(2560f, 1080f, scale);
            // contentW = 1920, contentH = 1080
            // contentX = (2560 - 1920)/2 = 320, contentY = 0
            Assert.AreEqual(320f, content.x, 0.001f);
            Assert.AreEqual(0f, content.y, 0.001f);
            Assert.AreEqual(1920f, content.width, 0.001f);
            Assert.AreEqual(1080f, content.height, 0.001f);
        }

        [Test]
        public void SafeArea_IntersectsAndMapsToDesign()
        {
            // 2340x1080 with left notch cutout of 120px
            // contentRect for 2340x1080 at scale 1.5 is x=210, y=0, w=1920, h=1080
            // safeArea = x=120, y=0, w=2220, h=1080
            Rect screenSafeArea = new Rect(120f, 0f, 2220f, 1080f);
            Rect safeDesign = DesignViewport.CalculateSafeDesignRect(2340f, 1080f, screenSafeArea);

            // safeScreen intersection with contentRect (210..2130) is 210..2130, so full width inside contentRect!
            Assert.GreaterOrEqual(safeDesign.x, 0f);
            Assert.LessOrEqual(safeDesign.xMax, DesignViewport.DesignWidth);
            Assert.GreaterOrEqual(safeDesign.y, 0f);
            Assert.LessOrEqual(safeDesign.yMax, DesignViewport.DesignHeight);
        }

        [Test]
        public void MinTouchTarget_SatisfiesBaselineAndDpFloor()
        {
            // When DPI <= 0, baseline 88 units
            float target0 = DesignViewport.CalculateMinTouchTargetDesignUnits(0f, 1.0f);
            Assert.AreEqual(88f, target0);

            // 1080p @ 400dpi, scale = 1.5: 44 * 400 / 160 / 1.5 = 73.33 units -> max(88, 73.33) = 88 units
            float target1 = DesignViewport.CalculateMinTouchTargetDesignUnits(400f, 1.5f);
            Assert.AreEqual(88f, target1);

            // Very high DPI tablet (e.g. 640dpi @ scale 1.0): 44 * 640 / 160 / 1.0 = 176 units
            float target2 = DesignViewport.CalculateMinTouchTargetDesignUnits(640f, 1.0f);
            Assert.AreEqual(176f, target2);
        }
    }
}
