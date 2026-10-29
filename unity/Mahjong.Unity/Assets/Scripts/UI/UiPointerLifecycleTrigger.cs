#nullable enable
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Mahjong.Unity.UI
{
    [RequireComponent(typeof(Selectable))]
    public sealed class UiPointerLifecycleTrigger : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IPointerClickHandler, ISubmitHandler
    {
        public void OnPointerDown(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            var canvas = GetComponentInParent<Canvas>();
            float scale = canvas != null ? canvas.scaleFactor : 1.0f;
            UiInputLifecycle.Instance.TryArm(eventData.pointerId, eventData.position, scale, gameObject);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            var canvas = GetComponentInParent<Canvas>();
            float scale = canvas != null ? canvas.scaleFactor : 1.0f;
            UiInputLifecycle.Instance.UpdateDrag(eventData.pointerId, eventData.position, scale);
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            long gen = UiInputLifecycle.Instance.PressGeneration;
            UiInputLifecycle.Instance.TryRelease(eventData.pointerId, gen, gameObject);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            UiInputLifecycle.Instance.NotifyPointerClick(gameObject);
        }

        public void OnSubmit(BaseEventData eventData)
        {
            UiInputLifecycle.Instance.NotifySubmit(gameObject);
        }
    }
}
