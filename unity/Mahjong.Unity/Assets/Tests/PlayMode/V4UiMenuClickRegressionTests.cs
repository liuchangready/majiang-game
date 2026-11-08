using System.Collections;
using System.Linq;
using Mahjong.Unity.UI.Prototype;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
public sealed class V4UiMenuClickRegressionTests
{
    private GameObject root;
    [UnitySetUp] public IEnumerator SetUp(){root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Prototypes/V4UiPrototype.prefab"));yield return null;}
    [UnityTearDown] public IEnumerator TearDown(){if(root!=null)Object.Destroy(root);yield return null;}
    private void Show(string name)=>typeof(V4UiPrototype).GetMethod(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(root.GetComponent<V4UiPrototype>(),null);
    private Button Named(string part)=>root.GetComponentsInChildren<Button>(true).Single(b=>b.gameObject.activeInHierarchy&&b.name.Contains(part));
    private string ReceiptText()=>root.GetComponentsInChildren<Text>(true).Single(t=>t.name=="Text · 预览确认").text;
    [UnityTest] public IEnumerator DrawerConfirmsZeroTenThirtyAndFortyPointPreviews()
    {
        Show("ShowPreparation");Show("ShowDrawer");Named("直接开局").onClick.Invoke();yield return null;Assert.That(ReceiptText(),Does.Contain("预览费用：0 点"));
        Show("ShowDrawer");Named("看破：").onClick.Invoke();yield return null;Assert.That(Named("确认施术").interactable,Is.True);Named("确认施术").onClick.Invoke();yield return null;Assert.That(ReceiptText(),Does.Contain("预览费用：10 点"));
        Show("ShowDrawer");Named("看破：").onClick.Invoke();var card=root.GetComponentsInChildren<Button>(true).First(b=>b.gameObject.activeInHierarchy&&b.name=="Button · ");card.onClick.Invoke();yield return null;Assert.That(root.GetComponentsInChildren<Text>(true).Any(t=>t.text=="本次消耗 30 点"),Is.True);card.onClick.Invoke();yield return null;Assert.That(root.GetComponentsInChildren<Text>(true).Any(t=>t.text=="本次消耗 0 点"),Is.True);card.onClick.Invoke();yield return null;Assert.That(root.GetComponentsInChildren<Text>(true).Any(t=>t.text=="本次消耗 30 点"),Is.True);Named("确认施术").onClick.Invoke();yield return null;Assert.That(ReceiptText(),Does.Contain("预览费用：30 点"));
        Show("ShowDrawer");Named("看破：").onClick.Invoke();yield return null;Assert.That(root.GetComponentsInChildren<Text>(true).Any(t=>t.text=="本次消耗 40 点"),Is.True);Named("确认施术").onClick.Invoke();yield return null;Assert.That(ReceiptText(),Does.Contain("预览费用：40 点"));
        Named("返回准备").onClick.Invoke();yield return null;Assert.That(root.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/Preparation").gameObject.activeSelf,Is.True);Show("ShowPlaying");Named("准备阶段预览").onClick.Invoke();yield return null;Assert.That(root.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/Preparation").gameObject.activeSelf,Is.True);
    }
    [UnityTest] public IEnumerator SinglePlayerButtonNavigatesFromSerializedPrefab()
    {
        var buttons=root.GetComponentsInChildren<Button>(true);Assert.That(buttons.Count(b=>b.transform.parent.name=="Main menu"),Is.EqualTo(6),"serialized menu must expose all six navigable entries");buttons.Single(b=>b.name.Contains("单机对局")).onClick.Invoke();yield return null;
        Assert.That(root.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/Preparation").gameObject.activeSelf,Is.True);Assert.That(root.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/Main menu").gameObject.activeSelf,Is.False);
    }
    [UnityTest] public IEnumerator PointerPositionOnMenuButtonNavigatesToPreparation()
    {
        yield return new WaitForSecondsRealtime(.8f);Canvas.ForceUpdateCanvases();var button=root.GetComponentsInChildren<Button>(true).Single(b=>b.name.Contains("单机对局"));
        var data=new PointerEventData(EventSystem.current){button=PointerEventData.InputButton.Left,position=RectTransformUtility.WorldToScreenPoint(null,button.transform.position)};var hits=new System.Collections.Generic.List<RaycastResult>();EventSystem.current.RaycastAll(data,hits);
        Assert.That(hits.Count,Is.GreaterThan(0));Assert.That(hits[0].gameObject,Is.EqualTo(button.gameObject));ExecuteEvents.Execute(button.gameObject,data,ExecuteEvents.pointerDownHandler);ExecuteEvents.Execute(button.gameObject,data,ExecuteEvents.pointerUpHandler);ExecuteEvents.Execute(button.gameObject,data,ExecuteEvents.pointerClickHandler);yield return null;
        Assert.That(root.transform.Find("Prototype Canvas/Prototype Content/Design stage · 1280×720/Preparation").gameObject.activeSelf,Is.True);
    }
}

