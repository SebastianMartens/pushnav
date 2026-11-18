# SPDX-License-Identifier: GPL-3.0-or-later
# This file is part of PushNav (fork additions; see
# specs/camera-server/SPEC_CAMERA_SERVER_WINDOWS.md section 8 and task 6.1).

"""Windows camera server selection: ``evf/camera/server_select.py``.

Everything here is pure path / setting logic, so it runs on every OS (CI: ubuntu and
windows) with exes faked as empty files.
"""

import logging

import pytest

from evf import paths
from evf.camera import server_select as ss
from evf.config.manager import DEFAULT_CONFIG, ConfigManager


@pytest.fixture
def exes(tmp_path):
    """Both servers present, in a dev-like layout."""
    dotnet = tmp_path / "camera" / "windows-dotnet" / "publish" / "camera_server_dotnet.exe"
    c = tmp_path / "camera" / "windows" / "camera_server.exe"
    for path in (dotnet, c):
        path.parent.mkdir(parents=True)
        path.touch()
    return dotnet, c


def plan(exes, config_value=None, env=None, windows=True, **overrides):
    dotnet, c = exes
    return ss.build_plan(
        config_value,
        env={} if env is None else env,
        windows=windows,
        dotnet_path=overrides.get("dotnet_path", dotnet),
        c_path=overrides.get("c_path", c),
    )


class TestSetting:
    @pytest.mark.parametrize("value", [None, "", "  ", "dotnet", "DotNet", " dotnet "])
    def test_default_and_dotnet_start_the_csharp_server(self, exes, value):
        p = plan(exes, value)
        assert p.current.name == "dotnet"
        assert p.current.path == exes[0]

    @pytest.mark.parametrize("value", ["c", "C", " c "])
    def test_c_starts_only_the_c_server(self, exes, value):
        p = plan(exes, value)
        assert p.current.name == "c"
        assert p.current.path == exes[1]

    def test_env_overrides_the_config_in_both_directions(self, exes):
        assert plan(exes, "dotnet", env={ss.ENV_SERVER: "c"}).current.name == "c"
        assert plan(exes, "c", env={ss.ENV_SERVER: "dotnet"}).current.name == "dotnet"

    def test_an_empty_env_value_is_ignored(self, exes):
        assert plan(exes, "c", env={ss.ENV_SERVER: "  "}).current.name == "c"

    def test_an_unknown_config_value_warns_and_uses_dotnet(self, exes, caplog):
        with caplog.at_level(logging.WARNING, logger=ss.logger.name):
            p = plan(exes, "ids")
        assert p.current.name == "dotnet"
        assert any("camera.windows_server" in r.message and "'ids'" in r.message for r in caplog.records)

    def test_an_unknown_env_value_warns_and_uses_dotnet_even_if_the_config_says_c(self, exes, caplog):
        with caplog.at_level(logging.WARNING, logger=ss.logger.name):
            p = plan(exes, "c", env={ss.ENV_SERVER: "bogus"})
        assert p.current.name == "dotnet"
        assert any(ss.ENV_SERVER in r.message for r in caplog.records)

    @pytest.mark.parametrize("setting", [None, "dotnet", "c"])
    def test_ignored_on_macos_and_linux(self, exes, setting):
        assert plan(exes, setting, env={ss.ENV_SERVER: "c"}, windows=False) is None


class TestNoCameraFallback:
    def test_exit_code_3_from_the_csharp_server_falls_back_to_the_c_server(self, exes):
        p = plan(exes, "dotnet")
        choice = p.fallback_after_exit(3)
        assert choice is not None and choice.name == "c" and choice.path == exes[1]

    @pytest.mark.parametrize("code", [0, 1, 2, -1, None])
    def test_any_other_exit_follows_the_normal_backoff(self, exes, code):
        assert plan(exes, "dotnet").fallback_after_exit(code) is None

    def test_the_c_setting_never_falls_back(self, exes):
        assert plan(exes, "c").fallback_after_exit(3) is None

    def test_without_a_c_server_there_is_nothing_to_fall_back_to(self, exes):
        p = plan(exes, "dotnet", c_path=exes[1].with_name("missing.exe"))
        assert p.current.name == "dotnet"
        assert p.fallback_after_exit(3) is None

    def test_the_working_server_is_remembered_and_does_not_fall_back_again(self, exes):
        p = plan(exes, "dotnet")
        p.switch_to(p.fallback_after_exit(3))
        assert p.current.name == "c"
        assert p.fallback_after_exit(3) is None


class TestMissingExecutables:
    def test_missing_csharp_exe_warns_with_path_and_build_hint_and_uses_the_c_server(self, exes, caplog):
        missing = exes[0].with_name("nope_dotnet.exe")
        with caplog.at_level(logging.WARNING, logger=ss.logger.name):
            p = plan(exes, "dotnet", dotnet_path=missing)
        assert p.current.name == "c"
        assert p.fallback_after_exit(3) is None
        message = next(r.message for r in caplog.records if "C# camera server not found" in r.message)
        assert str(missing) in message
        assert "build.bat" in message
        assert "Falling back to the C server" in message

    def test_missing_c_exe_warns_and_uses_the_csharp_server(self, exes, caplog):
        missing = exes[1].with_name("nope.exe")
        with caplog.at_level(logging.WARNING, logger=ss.logger.name):
            p = plan(exes, "c", c_path=missing)
        assert p.current.name == "dotnet"
        message = next(r.message for r in caplog.records if "C camera server not found" in r.message)
        assert str(missing) in message
        assert "Falling back to the C# server" in message

    def test_neither_exe_is_a_file_not_found_error_naming_both_paths(self, tmp_path):
        a, b = tmp_path / "x_dotnet.exe", tmp_path / "x_c.exe"
        with pytest.raises(FileNotFoundError) as info:
            ss.build_plan("dotnet", env={}, windows=True, dotnet_path=a, c_path=b)
        assert str(a) in str(info.value) and str(b) in str(info.value)


class TestHelpers:
    def test_stale_process_names_cover_both_servers(self):
        assert set(ss.stale_process_names("camera_server_dotnet.exe")) == {"camera_server.exe", "camera_server_dotnet.exe"}
        assert set(ss.stale_process_names("camera_server.exe")) == {"camera_server.exe", "camera_server_dotnet.exe"}
        assert "other_server.exe" in ss.stale_process_names("other_server.exe")

    def test_extra_args_go_to_the_csharp_server_only(self, exes):
        p = plan(exes, "dotnet")
        env = {ss.ENV_EXTRA_ARGS: "--backend fake --log-level debug"}
        assert ss.extra_args(p.current, env) == ["--backend", "fake", "--log-level", "debug"]
        assert ss.extra_args(ss.ServerChoice("c", exes[1]), env) == []
        assert ss.extra_args(p.current, {}) == []

    def test_extra_args_with_a_windows_path_keeps_the_backslashes(self, exes, monkeypatch):
        monkeypatch.setattr(ss, "is_windows", lambda: True)
        env = {ss.ENV_EXTRA_ARGS: r'--fake-images "C:\some dir\samples"'}
        assert ss.extra_args(ss.ServerChoice("dotnet", exes[0]), env) == ["--fake-images", r"C:\some dir\samples"]


class TestInstallLayouts:
    """``evf.paths.windows_camera_server_paths``: dev checkout vs Windows release directory."""

    def test_dev_layout(self, monkeypatch, tmp_path):
        monkeypatch.setattr(paths, "_WINDOWS_RELEASE", False)
        monkeypatch.setattr(paths, "_REPO_ROOT", tmp_path)
        dotnet, c = paths.windows_camera_server_paths()
        assert dotnet == tmp_path / "camera" / "windows-dotnet" / "publish" / "camera_server_dotnet.exe"
        assert c == tmp_path / "camera" / "windows" / "camera_server.exe"

    def test_release_layout_has_both_exes_next_to_each_other(self, monkeypatch, tmp_path):
        monkeypatch.setattr(paths, "_WINDOWS_RELEASE", True)
        monkeypatch.setattr(paths, "_RELEASE_ROOT", tmp_path)
        dotnet, c = paths.windows_camera_server_paths()
        assert dotnet == tmp_path / "camera_server_dotnet.exe"
        assert c == tmp_path / "camera_server.exe"

    def test_the_plan_resolves_the_default_layout(self, monkeypatch, tmp_path):
        monkeypatch.setattr(paths, "_WINDOWS_RELEASE", True)
        monkeypatch.setattr(paths, "_RELEASE_ROOT", tmp_path)
        (tmp_path / "camera_server_dotnet.exe").touch()
        (tmp_path / "camera_server.exe").touch()
        p = ss.build_plan("dotnet", env={}, windows=True)
        assert p.current.path == tmp_path / "camera_server_dotnet.exe"
        assert p.fallback_after_exit(3).path == tmp_path / "camera_server.exe"


class TestConfigDefault:
    def test_default_config_selects_dotnet(self):
        assert DEFAULT_CONFIG["camera"]["windows_server"] == "dotnet"

    def test_an_existing_config_without_the_key_gets_the_default(self, tmp_path):
        (tmp_path / "config.json").write_text(
            '{"version": 1, "camera": {"exposure": 200, "gain": 20, "extra_camera_ids": []}}'
        )
        config = ConfigManager(config_dir=tmp_path)
        assert config.get("camera", "windows_server") == "dotnet"
        assert config.exposure == 200  # the rest of the user's settings is untouched

    def test_a_user_value_is_kept(self, tmp_path):
        (tmp_path / "config.json").write_text('{"version": 1, "camera": {"windows_server": "c"}}')
        assert ConfigManager(config_dir=tmp_path).get("camera", "windows_server") == "c"
