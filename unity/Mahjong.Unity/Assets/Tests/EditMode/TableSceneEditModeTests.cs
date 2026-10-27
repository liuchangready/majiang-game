#nullable enable
using System;
using System.IO;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Mahjong.Unity.App;
using Mahjong.Unity.Table;
using Mahjong.Unity.UI.Session;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Mahjong.Unity.Tests.EditMode
{
    [TestFixture]
    public sealed class TableSceneEditModeTests
    {
        [Test]
        public void Step01_BuildTableScene_GeneratesAssetAndFileExists()
        {
#if UNITY_EDITOR
            TableSceneBuilder.BuildTableScene();
            Assert.IsTrue(File.Exists(TableSceneBuilder.TableScenePath), "Table.unity scene file must exist on disk.");
#endif
        }

        [Test]
        public void Step02_TableScene_HierarchyAndPresenterBindingsIntegrity()
        {
#if UNITY_EDITOR
            var scene = EditorSceneManager.OpenScene(TableSceneBuilder.TableScenePath, OpenSceneMode.Single);
            Assert.IsTrue(scene.IsValid(), "Scene must be valid.");

            var presenter = UnityEngine.Object.FindFirstObjectByType<TablePresenter>();
            Assert.IsNotNull(presenter, "TablePresenter must exist in Table.unity");

            var binding = UnityEngine.Object.FindFirstObjectByType<TableSceneBinding>();
            Assert.IsNotNull(binding, "TableSceneBinding must exist in Table.unity");
            Assert.AreEqual(presenter, binding!.Presenter, "TableSceneBinding must link to TablePresenter.");

            // Verify Camera and Canvas
            var cam = UnityEngine.Object.FindFirstObjectByType<Camera>();
            Assert.IsNotNull(cam, "Camera must exist in Table.unity");
            Assert.AreEqual("MainCamera", cam!.tag);

            var canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>();
            Assert.IsNotNull(canvas, "Canvas must exist in Table.unity");
            var scaler = canvas!.GetComponent<CanvasScaler>();
            Assert.IsNotNull(scaler, "CanvasScaler must exist");
            Assert.AreEqual(new Vector2(1280, 720), scaler!.referenceResolution);

            // Verify Serialized fields on TablePresenter
            var so = new SerializedObject(presenter!);
            Assert.IsNotNull(so.FindProperty("_sessionInfoText").objectReferenceValue, "_sessionInfoText bound");
            Assert.IsNotNull(so.FindProperty("_turnCountdownText").objectReferenceValue, "_turnCountdownText bound");
            Assert.IsNotNull(so.FindProperty("_backButton").objectReferenceValue, "_backButton bound");
            Assert.IsNotNull(so.FindProperty("_viewSettlementButton").objectReferenceValue, "_viewSettlementButton bound");
            Assert.IsNotNull(so.FindProperty("_nextRoundButton").objectReferenceValue, "_nextRoundButton bound");
            Assert.IsNotNull(so.FindProperty("_roundStatusText").objectReferenceValue, "_roundStatusText bound");
            Assert.IsNotNull(so.FindProperty("_wallRemainingText").objectReferenceValue, "_wallRemainingText bound");
            Assert.IsNotNull(so.FindProperty("_doraIndicatorsText").objectReferenceValue, "_doraIndicatorsText bound");
            Assert.IsNotNull(so.FindProperty("_lastDiscardText").objectReferenceValue, "_lastDiscardText bound");

            // 4 Seats
            var seatsProp = so.FindProperty("_seatViews");
            Assert.AreEqual(4, seatsProp.arraySize, "Must have 4 seat views");
            for (int i = 0; i < 4; i++)
            {
                var sp = seatsProp.GetArrayElementAtIndex(i);
                Assert.IsNotNull(sp.FindPropertyRelative("_root").objectReferenceValue, $"Seat {i} root bound");
                Assert.IsNotNull(sp.FindPropertyRelative("_seatNameText").objectReferenceValue, $"Seat {i} name bound");
                Assert.IsNotNull(sp.FindPropertyRelative("_scoreText").objectReferenceValue, $"Seat {i} score bound");
                Assert.IsNotNull(sp.FindPropertyRelative("_handText").objectReferenceValue, $"Seat {i} hand bound");
                Assert.IsNotNull(sp.FindPropertyRelative("_discardsText").objectReferenceValue, $"Seat {i} discards bound");
                Assert.IsNotNull(sp.FindPropertyRelative("_meldsText").objectReferenceValue, $"Seat {i} melds bound");
            }

            // Hand tiles & Action buttons
            Assert.IsNotNull(so.FindProperty("_ownHandPanelRoot").objectReferenceValue, "_ownHandPanelRoot bound");
            var handBtns = so.FindProperty("_handTileButtons");
            Assert.AreEqual(13, handBtns.arraySize, "13 closed hand tile buttons");
            Assert.IsNotNull(so.FindProperty("_drawnTileButton").objectReferenceValue, "_drawnTileButton bound");

            Assert.IsNotNull(so.FindProperty("_discardButton").objectReferenceValue, "_discardButton bound");
            Assert.IsNotNull(so.FindProperty("_chiButton").objectReferenceValue, "_chiButton bound");
            Assert.IsNotNull(so.FindProperty("_ponButton").objectReferenceValue, "_ponButton bound");
            Assert.IsNotNull(so.FindProperty("_kanButton").objectReferenceValue, "_kanButton bound");
            Assert.IsNotNull(so.FindProperty("_riichiButton").objectReferenceValue, "_riichiButton bound");
            Assert.IsNotNull(so.FindProperty("_tsumoButton").objectReferenceValue, "_tsumoButton bound");
            Assert.IsNotNull(so.FindProperty("_ronButton").objectReferenceValue, "_ronButton bound");
            Assert.IsNotNull(so.FindProperty("_passButton").objectReferenceValue, "_passButton bound");

            // Cheat dialogs
            Assert.IsNotNull(so.FindProperty("_cheatPanelRoot").objectReferenceValue, "_cheatPanelRoot bound");
            Assert.IsNotNull(so.FindProperty("_tileMarkDialog").objectReferenceValue, "_tileMarkDialog bound");
            Assert.IsNotNull(so.FindProperty("_sleeveBloomDialog").objectReferenceValue, "_sleeveBloomDialog bound");
            Assert.IsNotNull(so.FindProperty("_beamSwapDialog").objectReferenceValue, "_beamSwapDialog bound");
            Assert.IsNotNull(so.FindProperty("_preparationDialog").objectReferenceValue, "_preparationDialog bound");

            // Banners
            Assert.IsNotNull(so.FindProperty("_statusBannerText").objectReferenceValue, "_statusBannerText bound");
            Assert.IsNotNull(so.FindProperty("_animationBannerText").objectReferenceValue, "_animationBannerText bound");
#endif
        }

        [Test]
        public void Step03_TileTextHelper_StandardTerminologyMapping()
        {
            // Characters (万) 0..35
            Assert.AreEqual("1万", TileTextHelper.FormatTile(0));
            Assert.AreEqual("1万", TileTextHelper.FormatTile(3));
            Assert.AreEqual("9万", TileTextHelper.FormatTile(32));

            // Bamboo (索) 36..71
            Assert.AreEqual("1索", TileTextHelper.FormatTile(36));
            Assert.AreEqual("9索", TileTextHelper.FormatTile(68));

            // Dots (筒) 72..107
            Assert.AreEqual("1筒", TileTextHelper.FormatTile(72));
            Assert.AreEqual("9筒", TileTextHelper.FormatTile(104));

            // Winds (风) 108..123
            Assert.AreEqual("东", TileTextHelper.FormatTile(108));
            Assert.AreEqual("南", TileTextHelper.FormatTile(112));
            Assert.AreEqual("西", TileTextHelper.FormatTile(116));
            Assert.AreEqual("北", TileTextHelper.FormatTile(120));

            // Dragons (三元) 124..135
            Assert.AreEqual("白", TileTextHelper.FormatTile(124));
            Assert.AreEqual("发", TileTextHelper.FormatTile(128));
            Assert.AreEqual("中", TileTextHelper.FormatTile(132));

            // Marked tile string
            string marked = TileTextHelper.FormatMarkedTile(0, 3);
            Assert.IsTrue(marked.Contains("★1万"));
            Assert.IsTrue(marked.Contains("余3局"));

            // Actions & Melds
            Assert.AreEqual("吃", TileTextHelper.FormatMeldKind(PlayerMeldKind.Chi));
            Assert.AreEqual("碰", TileTextHelper.FormatMeldKind(PlayerMeldKind.Pon));
            Assert.AreEqual("暗杠", TileTextHelper.FormatMeldKind(PlayerMeldKind.ClosedKan));
            Assert.AreEqual("加杠", TileTextHelper.FormatMeldKind(PlayerMeldKind.AddedKan));

            Assert.AreEqual("打牌", TileTextHelper.FormatActionKind(PlayerActionKind.DiscardTile));
            Assert.AreEqual("立直", TileTextHelper.FormatActionKind(PlayerActionKind.DeclareRiichi));
            Assert.AreEqual("自摸", TileTextHelper.FormatActionKind(PlayerActionKind.DeclareTsumo));
            Assert.AreEqual("荣和", TileTextHelper.FormatActionKind(PlayerActionKind.DeclareRon));
        }

        [Test]
        public void Step04_TableSeatView_OpponentPrivacyWhitelistGuaranteed()
        {
            var go = new GameObject("TestSeatRoot");
            var nameTxt = new GameObject("Name").AddComponent<TextMeshProUGUI>();
            nameTxt.transform.SetParent(go.transform);
            var scoreTxt = new GameObject("Score").AddComponent<TextMeshProUGUI>();
            scoreTxt.transform.SetParent(go.transform);
            var handTxt = new GameObject("Hand").AddComponent<TextMeshProUGUI>();
            handTxt.transform.SetParent(go.transform);
            var badgeGo = new GameObject("TestBadge");
            badgeGo.transform.SetParent(go.transform);
            var discardsTxt = new GameObject("Discards").AddComponent<TextMeshProUGUI>();
            discardsTxt.transform.SetParent(go.transform);
            var meldsTxt = new GameObject("Melds").AddComponent<TextMeshProUGUI>();
            meldsTxt.transform.SetParent(go.transform);
            var ankanTxt = new GameObject("Ankan").AddComponent<TextMeshProUGUI>();
            ankanTxt.transform.SetParent(go.transform);

            var seatView = new TableSeatView();
            seatView.BindControls(go, nameTxt, scoreTxt, handTxt, badgeGo, discardsTxt, meldsTxt, ankanTxt);

            // Construct an opponent seat with 13 tiles, ankan placeholder, and discards
            var placeholderMeld = new PlayerMeldState(PlayerMeldKind.ClosedKan, Array.Empty<uint>(), null, -1, true, 0);
            var opponentSeat = new PlayerSeatState(
                1,
                true,
                13,
                false,
                new PlayerDiscardState[] { new PlayerDiscardState(0, 1, false, false) },
                new PlayerMeldState[] { placeholderMeld },
                false);

            seatView.UpdateOpponentSeat(opponentSeat, "下家", false, false, 25000);

            // Hand text must not reveal any tile IDs!
            Assert.IsTrue(handTxt.text.Contains("手牌: 13张"));
            Assert.IsTrue(handTxt.text.Contains("[牌]"));
            Assert.IsFalse(handTxt.text.Contains("1万"));
            Assert.IsFalse(handTxt.text.Contains("#"));

            // Ankan text must strictly state placeholder without carrying any tile IDs
            Assert.IsTrue(ankanTxt.text.Contains("暗杠: 1组 (各4张无身份占位)"));
            Assert.IsFalse(ankanTxt.text.Contains("0"));
            Assert.IsFalse(meldsTxt.text.Contains("1万"));

            UnityEngine.Object.DestroyImmediate(badgeGo);
            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        public void Step05_TablePresenter_ClearAllResetsDisplaysCleanly()
        {
            var go = new GameObject("PresenterGo");
            var presenter = go.AddComponent<TablePresenter>();
            presenter.ClearAll();

            Assert.IsNull(presenter.LatestView);
            Assert.AreEqual(0, presenter.AnimationQueue.PendingCount);

            UnityEngine.Object.DestroyImmediate(go);
        }
    }
}
