using System;
using System.Collections.Generic;
using UnityEngine;
using VerbalProcess;

namespace VerbalProcess
{
    [RequireComponent(typeof(RectTransform))]
    public class WordLayout : MonoBehaviour
    {
        [SerializeField] private float verticalGap = 50;
        [SerializeField] private float horizontalGap = 10;
        [SerializeField] private float leftPadding = 0; 
        [SerializeField] private float rightPadding = 0; 
        [SerializeField] private float verticalPadding = 0;

        private Vector2 postionToInstantiate;
        private float layoutWidth;
        private RectTransform rectTransform;

        private void Start()
        {
            rectTransform = GetComponent<RectTransform>();
            Initialize();
        }

        // 패널이 열리거나 칩을 배치하기 전에 명시적으로 호출하여 크기와 위치를 초기화합니다.
        public void Initialize()
        {
            if (rectTransform == null)
                rectTransform = GetComponent<RectTransform>();

            // sizeDelta.x 대신 rect.width를 사용하여 Stretch 앵커에서도 실제 가로 크기를 안전하게 가져옵니다.
            layoutWidth = rectTransform.rect.width;

            // UI 좌표계상 아래로 내려갈수록 Y좌표는 마이너스(-) 방향이 되므로 verticalPadding을 빼줍니다.
            postionToInstantiate = new Vector2(leftPadding, -verticalPadding);

            // 초기 크기 설정 (기본 패딩만큼 잡아둠)
            rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, verticalPadding);
        }

        public void AddChips(WordChip chip)
        {
            RectTransform chipRect = chip.GetComponent<RectTransform>();
            float chipWidth = chipRect.rect.width;

            // 다음 배치될 위치의 오른쪽 끝이 (전체너비 - 우측패딩)을 넘으면 줄바꿈을 수행합니다.
            // (가로 판단을 위해 verticalGap 대신 horizontalGap 또는 0을 사용하여 조건 검사)
            if ((postionToInstantiate.x + chipWidth) > (layoutWidth - rightPadding))
            {
                postionToInstantiate.x = leftPadding;
                postionToInstantiate.y -= verticalGap; // UI 좌표계 기준 아래로 내리기 위해 마이너스(-) 연산
            }

            // 먼저 올바르게 결정된 위치에 칩을 배치합니다.
            chipRect.localPosition = postionToInstantiate;

            // 배치한 이후에 다음 칩을 배치할 X좌표를 누적 계산합니다.
            postionToInstantiate.x += (chipWidth + horizontalGap);
        }

        // 모든 칩 배치가 끝난 후 Content(자신)의 높이를 배치 결과에 맞추어 동적으로 확장합니다.
        public void UpdateLayoutHeight()
        {
            // postionToInstantiate.y는 음수값이므로 절대값으로 변환하고,
            // 마지막 줄의 높이(verticalGap)와 아래 패딩(verticalPadding)을 더해 전체 높이를 구합니다.
            float finalHeight = Mathf.Abs(postionToInstantiate.y) + verticalGap + verticalPadding;
            rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, finalHeight);
        }

        public void ClearChips()
        {
            // Clear 시 다시 초기 상태 좌표와 높이로 되돌립니다.
            Initialize();
        }
    }
}