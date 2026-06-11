using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace VerbalProcess
{
    [RequireComponent(typeof(AudioSource))]
    public class Speaker : MonoBehaviour
    {
        [Header("Audio Settings")]
        [SerializeField] private int serverSampleRate = 44100;
        [SerializeField] private float volume = 1.0f;
        [SerializeField] private int bufferThresholdChunks = 3; // 큐에 쌓일 청크 개수 기준

        public Action OnPlaybackFinished; // 모든 버퍼 재생이 완료되었을 때 발생

        private AudioSource _audioSource;

        public class SubtitleChunk
        {
            public string subtitleText;
            public float[] audioData;
        }

        public Action<string> OnSubtitleTextChanged; // 자막 변경 시 발생

        // float 하나가 아니라 배열(청크) 단위로 관리하여 성능 극대화
        private ConcurrentQueue<SubtitleChunk> _audioChunkQueue = new ConcurrentQueue<SubtitleChunk>();

        // 현재 읽고 있는 청크의 인덱스
        private float[] _currentChunk = null;
        private int _chunkIndex = 0;

        private int _outputSampleRate;
        private float _lastSample = 0;
        private float _currentSample = 0;
        private float _t = 0;
        private bool _hasCurrentSample = false;

        private bool _isEndOfStream = false;
        private bool _playbackFinishedEventFired = true; // 시작 시에는 완료된 상태로 간주

        private string _pendingSubtitleText = "";
        private string _currentSubtitleText = "";
        private float _currentSubtitleProgress = 0.0f;

        // GC 할당 최적화용 캐시 변수들
        private string _lastSubtitleText = null;
        private string[] _cachedWords = null;
        private int _lastWordsToShowCount = -1;
        private string _displayedSubtitle = "";

        [Header("Subtitle Trim Settings")]
        [SerializeField] private int maxSubtitleCharacters = 85;
        [SerializeField] private int trimStepCharacters = 30;
        private int _trimStartWordIndex = 0;

        private void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = true; // 더미 클립 무한 반복

            _outputSampleRate = AudioSettings.outputSampleRate;

            // OnAudioFilterRead를 작동시키기 위한 무음 더미 클립 생성
            _audioSource.clip = AudioClip.Create("DummyStream", _outputSampleRate, 1, _outputSampleRate, false);
            _audioSource.Play(); // 스트림 대기 상태로 항시 켜둠
        }

        private void Update()
        {
            // 메인 스레드에서 재생 종료 이벤트 처리
            if (_isEndOfStream && !_playbackFinishedEventFired && _audioChunkQueue.IsEmpty && _currentChunk == null)
            {
                _playbackFinishedEventFired = true;
                _isEndOfStream = false;
                OnPlaybackFinished?.Invoke();
                Debug.Log("[Speaker] All audio playback finished. VAD can be re-enabled.");
            }

            UpdateSubtitlePacing();
        }

        private void UpdateSubtitlePacing()
        {
            // 스레드 안전한 Volatile 읽기 수행
            string rawText = System.Threading.Volatile.Read(ref _currentSubtitleText);
            float progress = System.Threading.Volatile.Read(ref _currentSubtitleProgress);

            if (string.IsNullOrEmpty(rawText))
            {
                if (!string.IsNullOrEmpty(_displayedSubtitle))
                {
                    _displayedSubtitle = "";
                    _lastSubtitleText = null;
                    _cachedWords = null;
                    _lastWordsToShowCount = -1;
                    OnSubtitleTextChanged?.Invoke(_displayedSubtitle);
                }
                return;
            }

            // 1. 자막 텍스트가 바뀐 경우에만 Split 및 캐싱 수행 (GC 극적 감소)
            if (rawText != _lastSubtitleText)
            {
                _lastSubtitleText = rawText;
                _cachedWords = rawText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                _lastWordsToShowCount = -1; // 진행 단어 수 변경 트리거 강제화
                _trimStartWordIndex = 0;    // 트리밍 인덱스 초기화
            }

            if (_cachedWords == null || _cachedWords.Length == 0)
            {
                if (_displayedSubtitle != rawText)
                {
                    _displayedSubtitle = rawText;
                    OnSubtitleTextChanged?.Invoke(_displayedSubtitle);
                }
                return;
            }

            // 2. 진행률에 따라 보여줄 단어 수 계산
            int wordsToShow = Mathf.Clamp(Mathf.CeilToInt(_cachedWords.Length * progress), 1, _cachedWords.Length);

            // 3. 단어 수가 실제로 변경될 때만 Join 수행하여 가비지 발생 차단
            if (wordsToShow != _lastWordsToShowCount)
            {
                _lastWordsToShowCount = wordsToShow;

                // 3-1. 현재 노출 예정인 구간(_trimStartWordIndex ~ wordsToShow)의 글자 수(공백 포함)를 미리 계산
                int projectedLength = 0;
                for (int i = _trimStartWordIndex; i < wordsToShow; i++)
                {
                    projectedLength += _cachedWords[i].Length;
                    if (i > _trimStartWordIndex) projectedLength += 1;
                }

                // 3-2. 임계값 초과 시 한 줄 분량(trimStepCharacters) 이상을 지울 때까지 시작 인덱스 이동
                if (projectedLength > maxSubtitleCharacters)
                {
                    int removedLength = 0;
                    while (_trimStartWordIndex < wordsToShow && removedLength < trimStepCharacters)
                    {
                        removedLength += _cachedWords[_trimStartWordIndex].Length + 1;
                        _trimStartWordIndex++;
                    }
                }

                // 3-3. 최종 범위의 단어 조인
                int count = wordsToShow - _trimStartWordIndex;
                _displayedSubtitle = count > 0 ? string.Join(" ", _cachedWords, _trimStartWordIndex, count) : "";
                OnSubtitleTextChanged?.Invoke(_displayedSubtitle);
            }
        }

        public void HandleSubtitleReceived(string subtitleText)
        {
            _pendingSubtitleText = subtitleText;
        }

        public void HandleAudioChunkReceived(byte[] pcmData)
        {
            int validBytes = pcmData.Length - (pcmData.Length % 4);
            if (validBytes == 0) return;
            float[] floatArray = new float[validBytes / 4];

            // 최적화: 개별 형변환 대신 BlockCopy로 메모리 통째로 복사 (초고속)
            Buffer.BlockCopy(pcmData, 0, floatArray, 0, validBytes);

            // 🌟 2~3ms 페이드 인/아웃 최적화 (패킷 경계 팝핑/클릭 노이즈 방어)
            int fadeLengthSamples = Mathf.RoundToInt(serverSampleRate * 0.002f); // 약 2.5ms 상당의 샘플 개수
            int actualFadeLength = Mathf.Min(fadeLengthSamples, floatArray.Length / 2);

            if (actualFadeLength > 0)
            {
                // Fade In (청크 시작부 점진적 볼륨 상승)
                for (int i = 0; i < actualFadeLength; i++)
                {
                    float factor = (float)i / actualFadeLength;
                    floatArray[i] *= factor;
                }

                // Fade Out (청크 종료부 점진적 볼륨 하강)
                for (int i = 0; i < actualFadeLength; i++)
                {
                    int index = floatArray.Length - 1 - i;
                    float factor = (float)i / actualFadeLength;
                    floatArray[index] *= factor;
                }
            }

            SubtitleChunk chunk = new SubtitleChunk
            {
                subtitleText = _pendingSubtitleText,
                audioData = floatArray
            };
            _pendingSubtitleText = ""; // 큐에 매핑한 후 임시 변수 비우기

            _audioChunkQueue.Enqueue(chunk);
            _playbackFinishedEventFired = false;
            _isEndOfStream = false;

            // 초기 버퍼링 확인 (로그 출력용)
            if (_audioChunkQueue.Count == bufferThresholdChunks)
            {
                Debug.Log($"[Speaker] Buffer threshold reached. Audio streaming stabilized.");
            }
        }

        /// <summary>
        /// 서버로부터 더 이상 오디오 청크가 오지 않음을 설정합니다.
        /// </summary>
        public void SetEndOfStream()
        {
            _isEndOfStream = true;
            _playbackFinishedEventFired = false; // 오디오가 아예 없었더라도 재생 완료 이벤트를 발생시키기 위해 false로 설정
            Debug.Log("[Speaker] End of stream signaled from server.");
        }

        public void StopAndClear()
        {
            // 큐 비우기
            while (_audioChunkQueue.TryDequeue(out _)) { }

            _currentChunk = null;
            _chunkIndex = 0;
            _lastSample = 0;
            _currentSample = 0;
            _t = 0;
            _hasCurrentSample = false;
            _isEndOfStream = false;
            _playbackFinishedEventFired = true;

            // 자막 관련 변수 초기화
            _pendingSubtitleText = "";
            System.Threading.Volatile.Write(ref _currentSubtitleText, "");
            System.Threading.Volatile.Write(ref _currentSubtitleProgress, 0.0f);
            
            _lastSubtitleText = null;
            _cachedWords = null;
            _lastWordsToShowCount = -1;
            _displayedSubtitle = "";
            _trimStartWordIndex = 0;
            OnSubtitleTextChanged?.Invoke("");

            Debug.Log("[Speaker] Audio buffer and subtitles cleared.");
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (_outputSampleRate == 0) return;

            float resampleRatio = (float)serverSampleRate / _outputSampleRate;

            for (int i = 0; i < data.Length; i += channels)
            {
                while (_t >= 1.0f || !_hasCurrentSample)
                {
                    // 청크 단위 데이터 수급 로직
                    if (_currentChunk == null || _chunkIndex >= _currentChunk.Length)
                    {
                        if (_audioChunkQueue.TryDequeue(out var chunk))
                        {
                            _currentChunk = chunk.audioData;
                            _chunkIndex = 0;
                            System.Threading.Volatile.Write(ref _currentSubtitleText, chunk.subtitleText);
                        }
                        else
                        {
                            // 버퍼 언더런 (데이터 부족)
                            _currentChunk = null; // 현재 청크 완료 표시
                            _hasCurrentSample = false;
                            System.Threading.Volatile.Write(ref _currentSubtitleText, "");
                            System.Threading.Volatile.Write(ref _currentSubtitleProgress, 0.0f);
                            break;
                        }
                    }

                    float nextSample = _currentChunk[_chunkIndex++];

                    // 진행률 업데이트
                    if (_currentChunk != null)
                    {
                        float progress = (float)_chunkIndex / _currentChunk.Length;
                        System.Threading.Volatile.Write(ref _currentSubtitleProgress, progress);
                    }

                    if (!_hasCurrentSample)
                    {
                        _currentSample = nextSample;
                        _lastSample = nextSample;
                        _hasCurrentSample = true;
                        _t = 0;
                    }
                    else
                    {
                        _lastSample = _currentSample;
                        _currentSample = nextSample;
                        _t -= 1.0f;
                        if (_t < 0) _t = 0;
                    }
                }

                // 부드러운 보간 계산
                float interpolatedSample = 0f;
                if (_hasCurrentSample)
                {
                    interpolatedSample = Mathf.Lerp(_lastSample, _currentSample, _t);
                    _t += resampleRatio;
                }
                else
                {
                    // 팝핑 노이즈 방지: 파형이 0으로 확 떨어지지 않고 아주 빠르게 감쇠(Fade-out)
                    _lastSample = Mathf.Lerp(_lastSample, 0, 0.1f);
                    interpolatedSample = _lastSample;
                }

                // 채널 복사 (모노 스트림을 스테레오 스피커로 출력 시 양쪽 복사)
                for (int c = 0; c < channels; c++)
                {
                    data[i + c] = interpolatedSample * volume;
                }
            }
        }
    }
}