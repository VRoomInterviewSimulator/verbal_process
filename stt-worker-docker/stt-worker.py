import os
import uvicorn
import io
import json
import asyncio
import numpy as np
import onnxruntime as ort
import httpx
import websockets
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from faster_whisper import WhisperModel
from pydantic import BaseModel
from dotenv import load_dotenv

load_dotenv()
app = FastAPI()

# TTS Worker WebSocket URL (기본값: localhost:8001/ws/tts)
TTS_WORKER_WS_URL = os.getenv("TTS_WORKER_WS_URL", "ws://host.docker.internal:8001/ws/tts")

# 모델 로드 (Faster-Whisper & Silero VAD)
base_dir = os.path.dirname(os.path.abspath(__file__))
whisper_model_path = os.path.join(base_dir, "model", "whisper")
model = WhisperModel(whisper_model_path, device="cuda", compute_type="float16", local_files_only=True)

# Silero VAD 모델 로드
vad_model_path = os.path.join(base_dir, "model", "silero_vad", "silero_vad.onnx")
try:
    vad_sess = ort.InferenceSession(vad_model_path, providers=['CPUExecutionProvider'])
except Exception as e:
    print(f"VAD Model not found or error at {vad_model_path}: {e}")
    vad_sess = None

class InterviewFeatures(BaseModel):
    speakingTime: float
    pauseCount: int
    averageVolume: float

# Silero VAD의 모델 RNN 상태값: [2, 1, 128] 형태의 제로 텐서
silero_state = np.zeros((2, 1, 128), dtype=np.float32)

def validate_voice(audio_bytes):
    """Silero VAD를 사용하여 음성 유무 판별 (16kHz, Mono 가정)"""
    global silero_state # 상태 유지를 위해 global 사용
    if vad_sess is None: return True

    try:
        # 데이터가 'RIFF'로 시작하면 WAV 헤더(44바이트) 제거
        if audio_bytes[:4] == b'RIFF':
            pcm_data = audio_bytes[44:]
        else:
            pcm_data = audio_bytes

        audio_int16 = np.frombuffer(pcm_data, dtype=np.int16)
        audio_float32 = audio_int16.astype(np.float32) / 32768.0

        # Silero VAD는 512
        # 청크 전체를 512 단위로 쪼개서 하나라도 음성이면 True 반환
        window_size = 512
        is_speech = False
        for i in range(0, len(audio_float32) - window_size + 1, window_size):
            input_data = audio_float32[i:i+window_size].reshape(1, -1)
            ort_inputs = {
                "input": input_data,
                "sr": np.array([16000], dtype=np.int64),
                "state": silero_state
            }
            out, new_state = vad_sess.run(None, ort_inputs)
            silero_state = new_state # 다음 청크를 위해 상태 업데이트

            prob = out[0][0]
            if prob > 0.4: # 하나라도 음성 구간이 있으면 True
                is_speech = True

        return is_speech
    except Exception as e:
        print(f"VAD Error: {e}")
        return True

@app.websocket("/ws/interview")
async def websocket_endpoint(websocket: WebSocket):
    await websocket.accept()
    session_id = websocket.query_params.get("session_id", "default")
    print(f"WebSocket Connected (PCM Stream Mode) - Session ID: {session_id}")

    audio_buffer = bytearray()
    consecutive_silence_count = 0
    # 0.3초 청크 기준 3번 연속 침묵 시 종료, 유니티(1.5초 failsafe)보다 더 빠른 능동적 종료 가능
    SILENCE_LIMIT = 3
    
    global silero_state

    # TTS Worker(백엔드)와의 지속 WebSocket 연결 및 백그라운드 릴레이 관리
    tts_ws = None

    async def get_or_connect_tts_ws():
        nonlocal tts_ws
        if tts_ws is None or tts_ws.state != websockets.State.OPEN:
            print(f"[{session_id}] TTS Worker connection offline. Connecting...")
            try:
                ws_url_with_sid = f"{TTS_WORKER_WS_URL}?session_id={session_id}"
                tts_ws = await websockets.connect(ws_url_with_sid, max_size=None)
                print(f"[{session_id}] Persistent connection to TTS Worker established.")
            except Exception as e:
                print(f"[{session_id}] Failed to establish connection to TTS Worker: {e}")
                tts_ws = None
        return tts_ws

    # 백그라운드 릴레이 루프 (지속 수신 및 즉각 유니티 패스스루)
    async def tts_relay_loop():
        nonlocal tts_ws
        while True:
            try:
                ws = await get_or_connect_tts_ws()
                if ws is None:
                    await asyncio.sleep(1.0)
                    continue

                async for msg in ws:
                    if isinstance(msg, str):
                        try:
                            event = json.loads(msg)
                            if event.get("type") == "end":
                                # Unity로 오디오 종료 전송
                                await websocket.send_json({"type": "tts_end"})
                            elif event.get("type") == "subtitle":
                                await websocket.send_text(msg)
                        except json.JSONDecodeError:
                            pass
                    else:
                        await websocket.send_bytes(msg)
            except websockets.exceptions.ConnectionClosed:
                print(f"[{session_id}] TTS Worker connection closed in relay loop.")
                tts_ws = None
            except Exception as ex:
                print(f"[{session_id}] Error in tts_relay_loop: {ex}")
                tts_ws = None
                await asyncio.sleep(1.0)

    # 백그라운드 릴레이 태스크 시작
    relay_task = asyncio.create_task(tts_relay_loop())

    try:
        while True:
            message = await websocket.receive()

            if "bytes" in message:
                chunk = message["bytes"]

                # 오디오 데이터 오염 방지: 첫 청크만 헤더 포함, 나머지는 PCM만 병합
                if chunk[:4] == b'RIFF':
                    # 새 발화 시작 시 버퍼 초기화 (클라이언트가 발화 시작 때 헤더를 보냄)
                    if len(audio_buffer) > 0:
                        print("New header received. Resetting buffer.")
                    audio_buffer = bytearray(chunk)
                    silero_state = np.zeros((2, 1, 128), dtype=np.float32) # VAD 상태 초기화
                else:
                    audio_buffer.extend(chunk)

                # 실시간 VAD 체크 (전체 청크 분석)
                if validate_voice(chunk):
                    consecutive_silence_count = 0
                else:
                    consecutive_silence_count += 1

                # 서버 사이드 조기 종료 감지
                if consecutive_silence_count >= SILENCE_LIMIT:
                    if len(audio_buffer) > 32000: # 최소 1초 이상 데이터가 있을 때만
                        print(f"VAD detected {SILENCE_LIMIT} consecutive silences. Requesting end...")
                        await websocket.send_json({"type": "request_end"})
                        consecutive_silence_count = 0 

            elif "text" in message:
                print(f"Raw Unity JSON: {message}")
                data = json.loads(message["text"])
                msg_type = data.get("type")
                
                if msg_type == "discard":
                    print("Discard request received. Clearing buffers.")
                    audio_buffer = bytearray()
                    consecutive_silence_count = 0
                    silero_state = np.zeros((2, 1, 128), dtype=np.float32)
                    try:
                        await websocket.send_json({"type": "tts_end"})
                    except:
                        pass
                    continue

                elif msg_type == "send_anyway":
                    text_to_send = data.get("text", "")
                    features = data.get("features")
                    final_dto = {
                        "sttText": text_to_send,
                        "speakingTime": features.get("speakingTime", 0) if features else 0,
                        "pauseCount": features.get("pauseCount", 0) if features else 0,
                        "averageVolume": features.get("averageVolume", 0) if features else 0
                    }
                    print(f"Send Anyway request received. Directly processing TTS for: {text_to_send}")
                    await websocket.send_json({"type": "final", "data": final_dto})
                    
                    if text_to_send:
                        # TTS/LLM 전송 파트 수행 (아래의 공통 TTS 로직으로 이동하기 위해 변수 설정)
                        final_text = text_to_send
                    else:
                        print("Empty text for send_anyway. Skipping TTS.")
                        try:
                            await websocket.send_json({"type": "tts_end"})
                        except:
                            pass
                        continue

                elif msg_type == "utterance_end":
                    if len(audio_buffer) == 0:
                        # 오디오 데이터가 없는 경우 VAD 해제 및 무시
                        try:
                            await websocket.send_json({"type": "tts_end"})
                        except:
                            pass
                        continue

                    print(f"Utterance End received. Transcribing {len(audio_buffer)} bytes...")
                    try:
                        # WAV 헤더를 제외한 순수 PCM 추출 및 float32 변환
                        raw_pcm = audio_buffer[44:] if audio_buffer[:4] == b'RIFF' else audio_buffer
                        audio_np = np.frombuffer(raw_pcm, dtype=np.int16).astype(np.float32) / 32768.0
                        
                        # Whisper 추론 (비동기 스레드에서 실행하여 소켓 블로킹 방지)
                        def transcribe_task(audio):
                            segments, _ = model.transcribe(
                                audio, 
                                language="ko", 
                                beam_size=5, 
                                vad_filter=True, 
                                word_timestamps=True
                            )
                            words_info = []
                            full_text_segments = []
                            for segment in segments:
                                full_text_segments.append(segment.text)
                                if segment.words:
                                    for w in segment.words:
                                        words_info.append({
                                            "word": w.word.strip(),
                                            "probability": w.probability
                                        })
                            return " ".join(full_text_segments).strip(), words_info

                        final_text, words_info = await asyncio.to_thread(transcribe_task, audio_np)
                        features = data.get("features")
                        is_correction = data.get("mode") == "correction"
                        
                        # 단어 병합/치환 로직 (수정 모드인 경우)
                        if is_correction:
                            original_words = data.get("original_words", [])
                            target_range = data.get("target_range", [0, 0])
                            new_words = [w["word"] for w in words_info]
                            
                            start_idx, end_idx = target_range[0], target_range[1]
                            if 0 <= start_idx <= end_idx < len(original_words):
                                merged_words = original_words[:start_idx] + new_words + original_words[end_idx+1:]
                            else:
                                merged_words = new_words if new_words else original_words
                                
                            final_text = " ".join(merged_words).strip()
                            print(f"[Correction Merged] Original: {original_words} -> Target Range: {target_range} -> New: {new_words} -> Result: {final_text}")
                        
                        # 평균 신뢰도 계산
                        avg_confidence = sum([w["probability"] for w in words_info]) / len(words_info) if words_info else 1.0
                        print(f"STT Success: '{final_text}' (Confidence: {avg_confidence:.2f})")

                        # 수정 모드가 아닌 일반 모드일 때, 신뢰도가 임계값 미만이면 교정 요청 전송
                        if not is_correction and avg_confidence < 0.75:
                            print(f"Low confidence ({avg_confidence:.2f} < 0.75). Sending correction request to Unity.")
                            final_dto = {
                                "sttText": final_text,
                                "speakingTime": features["speakingTime"] if features else 0.0,
                                "pauseCount": features["pauseCount"] if features else 0,
                                "averageVolume": features["averageVolume"] if features else 0.0
                            }
                            words_list = [w["word"] for w in words_info]
                            confidences_list = [w["probability"] for w in words_info]
                            
                            await websocket.send_json({
                                "type": "correction_request",
                                "data": final_dto,
                                "words": words_list,
                                "word_confidences": confidences_list
                            })
                            
                            # 교정 요청 단계에서는 클라이언트가 재발화하기 전까지 VAD를 꺼두어야 하므로, tts_end를 보내지 않습니다.
                            audio_buffer = bytearray()
                            consecutive_silence_count = 0
                            silero_state = np.zeros((2, 1, 128), dtype=np.float32)
                            continue

                        # 일반 모드이면서 신뢰도가 양호하거나, 수정 모드인 경우 다음 LLM/TTS 파이프라인으로 전송
                        final_dto = {
                            "sttText": final_text,
                            "speakingTime": features["speakingTime"] if features else 0.0,
                            "pauseCount": features["pauseCount"] if features else 0,
                            "averageVolume": features["averageVolume"] if features else 0.0
                        }
                        await websocket.send_json({"type": "final", "data": final_dto})

                    except Exception as e:
                        print(f"Transcription/Processing Error: {e}")
                        try:
                            await websocket.send_json({"type": "tts_end"})
                        except:
                            pass
                        audio_buffer = bytearray()
                        consecutive_silence_count = 0
                        silero_state = np.zeros((2, 1, 128), dtype=np.float32)
                        continue

                else:
                    # 알 수 없는 메시지 유형 무시
                    continue

                # ==================== TTS / LLM Streaming Pipeline ====================
                if not final_text:
                    print("STT 결과 문자열이 빈 값입니다. TTS/LLM 요청을 스킵합니다.")
                    try:
                        await websocket.send_json({"type": "tts_end"})
                    except:
                        pass
                else:
                    # 2. TTS Worker에 요청하여 백그라운드 태스크가 수신할 수 있게 전송만 수행 (1회 재시도 보장)
                    print(f"Requesting TTS for: {final_text}")
                    sent_successfully = False
                    for attempt in range(2):
                        try:
                            ws = await get_or_connect_tts_ws()
                            if ws is not None:
                                await ws.send(json.dumps({"text": final_text, "session_id": session_id}))
                                print(f"Sent text to TTS Worker: {final_text}")
                                sent_successfully = True
                                break
                            else:
                                raise Exception("TTS WebSocket is None")
                        except Exception as tts_e:
                            print(f"[Attempt {attempt+1}] Failed to send text to TTS Worker: {tts_e}")
                            tts_ws = None

                    if not sent_successfully:
                        print("Failed to send text to TTS Worker after 2 attempts. Sending fallback tts_end to Unity.")
                        try:
                            await websocket.send_json({"type": "tts_end"})
                        except:
                            pass
                
                audio_buffer = bytearray()
                consecutive_silence_count = 0
                silero_state = np.zeros((2, 1, 128), dtype=np.float32) # VAD 상태 초기화

    except WebSocketDisconnect:
        print("WebSocket Disconnected")
    except asyncio.CancelledError:
        print("WebSocket connection cancelled (Server Shutdown)")
        raise
    except Exception as e:
        print(f"Critical Error: {e}")
        try: await websocket.close()
        except: pass
    finally:
        # 백그라운드 태스크 종료 및 TTS 지속 웹소켓 종료
        relay_task.cancel()
        if tts_ws is not None:
            try:
                if tts_ws.state == websockets.State.OPEN:
                    await tts_ws.close()
                print("Persistent connection to TTS Worker closed.")
            except:
                pass

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
