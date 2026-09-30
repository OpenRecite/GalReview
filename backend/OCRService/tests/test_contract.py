"""OCRService 最小契约测试（P2/T12）。

覆盖：healthz、网关密钥中间件、文件类型/模式/大小校验、响应形状、
任务进度/取消，以及固定小样例图 + mock OCR 引擎的期望文本契约。
不测真实 OCR 精度；不加载 PaddleOCR/PaddleX 模型。

运行：
    python -m pytest backend/OCRService/tests/test_contract.py -q
或  python -m unittest backend.OCRService.tests.test_contract
（若环境无 pytest，unittest 同样可跑。）
"""

from __future__ import annotations

import io
import sys
import types
import unittest
from pathlib import Path

# app.py 在 import 时会拉起 fitz / paddleocr。测试环境不装这些重依赖，
# 先注入桩模块，保证契约测试只验证 API 形状与错误码。
if "fitz" not in sys.modules:
    _fitz = types.ModuleType("fitz")

    class _FakePage:
        def get_pixmap(self, matrix=None, alpha=False):  # noqa: ANN001, ARG002
            raise RuntimeError("PDF rendering is not available in contract tests")

    class _FakeDoc:
        page_count = 0

        def __iter__(self):
            return iter(())

        def close(self):  # noqa: ANN201
            return None

    _fitz.open = lambda *_args, **_kwargs: _FakeDoc()  # noqa: E731
    _fitz.Matrix = lambda *_args, **_kwargs: None  # noqa: E731
    sys.modules["fitz"] = _fitz

if "paddleocr" not in sys.modules:
    _paddleocr = types.ModuleType("paddleocr")

    class _FakeEngine:
        def __init__(self, **_kwargs):
            pass

        def predict(self, *_args, **_kwargs):
            return []

    _paddleocr.PaddleOCR = _FakeEngine
    _paddleocr.FormulaRecognitionPipeline = _FakeEngine
    sys.modules["paddleocr"] = _paddleocr

# 密钥必须在 import app 之前就位。
import os

os.environ.setdefault("GATEWAY_KEY", "test-gateway-key")
os.environ.setdefault("PADDLE_PDX_CACHE_HOME", str(Path(__file__).resolve().parent / "models"))

_OCR_DIR = Path(__file__).resolve().parents[1]
if str(_OCR_DIR) not in sys.path:
    sys.path.insert(0, str(_OCR_DIR))

from fastapi.testclient import TestClient  # noqa: E402

import app as ocr_app  # noqa: E402

GATEWAY_KEY = os.environ["GATEWAY_KEY"]
AUTH_HEADERS = {"X-Gateway-Key": GATEWAY_KEY}

# 固定样例图：纯色小 PNG（合法 PNG 头），配合 mock 引擎断言期望文本。
SAMPLE_PNG = bytes.fromhex(
    "89504e470d0a1a0a0000000d494844520000000100000001080200000090"
    "7753de0000000c4944415408d763f8cfc000000301010018dd8db000000000"
    "49454e44ae426082"
)
EXPECTED_LINES = ["Hello", "GalReview"]


def _reset_state() -> None:
    ocr_app.JOB_PROGRESS.clear()
    ocr_app.CANCELLED_JOBS.clear()


class _FakeTextRegion:
    def __init__(self, text: str, left: float, top: float, right: float, bottom: float):
        self.text = text
        self.left = left
        self.top = top
        self.right = right
        self.bottom = bottom


def _fake_recognize_text_regions(image_path, mode):  # noqa: ANN001, ARG001
    # 固定样例图 → 期望文本的契约桩。两行垂直间距 >18，避免被 merge 成同一视觉行。
    return [
        _FakeTextRegion("Hello", 0, 0, 50, 10),
        _FakeTextRegion("GalReview", 0, 40, 80, 50),
    ]


def _fake_recognize_formula_regions(image_path):  # noqa: ANN001, ARG001
    return []


class OcrContractTests(unittest.TestCase):
    def setUp(self):
        _reset_state()
        self.client = TestClient(ocr_app.app, raise_server_exceptions=False)
        self._orig_text = ocr_app.recognize_text_regions
        self._orig_formula = ocr_app.recognize_formula_regions
        ocr_app.recognize_text_regions = _fake_recognize_text_regions
        ocr_app.recognize_formula_regions = _fake_recognize_formula_regions

    def tearDown(self):
        ocr_app.recognize_text_regions = self._orig_text
        ocr_app.recognize_formula_regions = self._orig_formula
        _reset_state()

    # ---------- healthz ----------

    def test_healthz_is_public_and_returns_live(self):
        response = self.client.get("/healthz")
        self.assertEqual(200, response.status_code)
        self.assertEqual({"status": "live"}, response.json())

    # ---------- 网关密钥中间件 ----------

    def test_missing_gateway_key_is_rejected_with_detail_envelope(self):
        response = self.client.get("/v1/ocr/jobs/any")
        self.assertEqual(401, response.status_code)
        body = response.json()
        self.assertIn("detail", body)
        self.assertIn("X-Gateway-Key", body["detail"])

    def test_wrong_gateway_key_is_rejected(self):
        response = self.client.get(
            "/v1/ocr/jobs/any", headers={"X-Gateway-Key": "not-the-key"}
        )
        self.assertEqual(401, response.status_code)

    # ---------- 文件类型 / 模式 / 大小校验 ----------

    def test_unsupported_media_type_returns_415(self):
        response = self.client.post(
            "/v1/ocr",
            headers=AUTH_HEADERS,
            files={"file": ("notes.txt", io.BytesIO(b"plain text"), "text/plain")},
        )
        self.assertEqual(415, response.status_code)
        self.assertIn("PDF, JPG and PNG", response.json()["detail"])

    def test_invalid_ocr_mode_returns_400(self):
        response = self.client.post(
            "/v1/ocr",
            headers={**AUTH_HEADERS, "X-Ocr-Mode": "turbo"},
            files={"file": ("a.png", io.BytesIO(SAMPLE_PNG), "image/png")},
        )
        self.assertEqual(400, response.status_code)
        self.assertIn("quick or standard", response.json()["detail"])

    def test_oversized_upload_returns_413(self):
        payload = b"\x89PNG\r\n\x1a\n" + b"0" * (ocr_app.MAX_UPLOAD_BYTES + 1)
        response = self.client.post(
            "/v1/ocr",
            headers=AUTH_HEADERS,
            files={"file": ("big.png", io.BytesIO(payload), "image/png")},
        )
        self.assertEqual(413, response.status_code)
        self.assertIn("10 MB", response.json()["detail"])

    # ---------- 响应形状：固定小图 + mock 引擎 → 期望文本 ----------

    def test_png_with_mocked_engine_returns_expected_lines(self):
        response = self.client.post(
            "/v1/ocr",
            headers={**AUTH_HEADERS, "X-Ocr-Mode": "quick", "X-Ocr-Job-Id": "job-png-1"},
            files={"file": ("sample.png", io.BytesIO(SAMPLE_PNG), "image/png")},
        )
        self.assertEqual(200, response.status_code)
        body = response.json()
        self.assertIn("pages", body)
        self.assertEqual(1, len(body["pages"]))
        page = body["pages"][0]
        self.assertEqual(1, page["pageNumber"])
        self.assertEqual(EXPECTED_LINES, page["lines"])
        self.assertIn("formulas", page)

    def test_jpeg_magic_bytes_are_accepted_even_with_generic_content_type(self):
        jpeg = b"\xff\xd8\xff\xe0" + b"\x00" * 32
        response = self.client.post(
            "/v1/ocr",
            headers={**AUTH_HEADERS, "X-Ocr-Mode": "quick"},
            files={"file": ("scan.bin", io.BytesIO(jpeg), "application/octet-stream")},
        )
        self.assertEqual(200, response.status_code)
        self.assertEqual(EXPECTED_LINES, response.json()["pages"][0]["lines"])

    def test_quick_mode_skips_formula_phase(self):
        phases: list[str] = []
        ocr_app.recognize_formula_regions = lambda path: phases.append("formula") or []

        response = self.client.post(
            "/v1/ocr",
            headers={**AUTH_HEADERS, "X-Ocr-Mode": "quick", "X-Ocr-Job-Id": "job-quick"},
            files={"file": ("a.png", io.BytesIO(SAMPLE_PNG), "image/png")},
        )
        self.assertEqual(200, response.status_code)
        self.assertEqual([], phases)

    # ---------- 进度 / 取消 ----------

    def test_unknown_job_progress_shape(self):
        response = self.client.get("/v1/ocr/jobs/never-seen", headers=AUTH_HEADERS)
        self.assertEqual(200, response.status_code)
        self.assertEqual(
            {
                "status": "UNKNOWN",
                "currentPage": 0,
                "totalPages": 0,
                "phase": "WAITING",
            },
            response.json(),
        )

    def test_cancel_marks_job_cancelled(self):
        response = self.client.post(
            "/v1/ocr/jobs/job-x/cancel", headers=AUTH_HEADERS
        )
        self.assertEqual(202, response.status_code)
        self.assertEqual(
            {"jobId": "job-x", "status": "CANCELLED"}, response.json()
        )
        progress = self.client.get("/v1/ocr/jobs/job-x", headers=AUTH_HEADERS).json()
        self.assertEqual("CANCELLED", progress["status"])
        self.assertEqual("CANCELLED", progress["phase"])

    def test_cancelled_job_cannot_continue(self):
        ocr_app.CANCELLED_JOBS.add("job-stop")
        with self.assertRaises(ocr_app.HTTPException) as ctx:
            ocr_app.ensure_not_cancelled("job-stop")
        self.assertEqual(409, ctx.exception.status_code)

    def test_successful_run_marks_progress_succeeded(self):
        response = self.client.post(
            "/v1/ocr",
            headers={**AUTH_HEADERS, "X-Ocr-Mode": "quick", "X-Ocr-Job-Id": "job-ok"},
            files={"file": ("a.png", io.BytesIO(SAMPLE_PNG), "image/png")},
        )
        self.assertEqual(200, response.status_code)
        progress = ocr_app.JOB_PROGRESS["job-ok"]
        self.assertEqual("SUCCEEDED", progress["status"])
        self.assertEqual("COMPLETED", progress["phase"])
        self.assertEqual(1, progress["totalPages"])

    # ---------- 纯函数契约 ----------

    def test_is_choice_label_extracts_parentheses_markers(self):
        self.assertEqual("(A)", ocr_app.is_choice_label("(A) 正确答案"))
        self.assertEqual("(c)", ocr_app.is_choice_label("  (c)"))
        self.assertIsNone(ocr_app.is_choice_label("A) 不是选项标记"))

    def test_rect_normalizes_and_rejects_bad_boxes(self):
        self.assertEqual((0.0, 0.0, 10.0, 5.0), ocr_app.rect([10, 5, 0, 0]))
        self.assertIsNone(ocr_app.rect([1, 2]))
        self.assertIsNone(ocr_app.rect(None))

    def test_result_to_payload_unwraps_res_key(self):
        self.assertEqual({"rec_texts": ["x"]}, ocr_app.result_to_payload({"res": {"rec_texts": ["x"]}}))
        self.assertEqual({}, ocr_app.result_to_payload(123))


if __name__ == "__main__":
    unittest.main()
