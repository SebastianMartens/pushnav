# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_CONTROLS.md sections 4.9 and 5.5, task 4.10).
"""The edge arrow sits 68 px inside a 1280 px wide frame and scales with the image width."""

from __future__ import annotations

import pytest

from tests.test_webserver_payload import _geometry, _server


@pytest.mark.parametrize("w,h,fov,margin", [
    pytest.param(1280, 720, 8.86, 68.0, id="1280-unchanged"),
    pytest.param(1936, 1216, 25.6, 68.0 * 1936 / 1280, id="1936"),
    pytest.param(968, 608, 25.6, 68.0 * 968 / 1280, id="968"),
])
def test_edge_arrow_margin_scales_with_the_image_width(tmp_path, w, h, fov, margin):
    # Target far north of the pointing: the arrow lands on the top edge of the inset rectangle.
    nav = _server(tmp_path, _geometry(w, h, fov), target=(100.0, 60.0))._build_payload()["nav"]
    assert nav["in_fov"] is False
    assert nav["edge_y"] == pytest.approx(margin)
    assert nav["edge_angle_deg"] == pytest.approx(0.0)


@pytest.mark.parametrize("w,h,fov,margin", [
    pytest.param(1280, 720, 8.86, 68.0, id="1280-unchanged"),
    pytest.param(1936, 1216, 25.6, 68.0 * 1936 / 1280, id="1936"),
])
def test_target_behind_the_camera_uses_the_same_margin(tmp_path, w, h, fov, margin):
    nav = _server(tmp_path, _geometry(w, h, fov), target=(280.0, 0.0))._build_payload()["nav"]
    assert nav["pixel_x"] is None
    inset_x = min(nav["edge_x"], w - nav["edge_x"])
    inset_y = min(nav["edge_y"], h - nav["edge_y"])
    assert min(inset_x, inset_y) == pytest.approx(margin)
