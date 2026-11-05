# -*- coding: utf-8 -*-
"""BlockCraft Launcher v0.6.0 — complete single-file source.
Merged from core.py, i18n.py, and app.py without changing runtime behavior.
Run: python -m pip install -r requirements.txt && python BlockCraftLauncher_完整版.py
The official Microsoft login still requires an approved application Client ID.
"""
from __future__ import annotations



# ============================================================================
# Core: downloads, game instances, auth, Modrinth
# ============================================================================

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import hmac
import hashlib
import functools
import inspect
import json
import os
from pathlib import Path
import re
import shutil
import sys
import subprocess
import tempfile
import threading
import time
from typing import Any, Callable
from urllib.parse import quote, urlparse, urlsplit, urlunsplit, parse_qs
import uuid

import requests

APP_NAME = "BlockCraftLauncher"
APP_VERSION = "0.6.0"
USER_AGENT = f"BlockCraftLauncher/{APP_VERSION} (Minecraft Java desktop launcher)"
OFFICIAL_MANIFEST = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
MIRROR_MANIFEST = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"
MODRINTH_API = "https://api.modrinth.com/v2"
MODRINTH_KINDS = {"mod": "mods", "shader": "shaderpacks", "resourcepack": "resourcepacks"}
LOADERS = {"原版": "vanilla", "Fabric": "fabric", "Forge": "forge", "NeoForge": "neoforge", "Quilt": "quilt"}
Progress = Callable[[str, int | None, int | None], None]


def _noop(message: str, current: int | None = None, total: int | None = None) -> None:
    pass


def app_data_dir() -> Path:
    if os.name == "nt":
        root = Path(os.environ.get("APPDATA", str(Path.home() / "AppData" / "Roaming")))
    elif os.sys.platform == "darwin":
        root = Path.home() / "Library" / "Application Support"
    else:
        root = Path(os.environ.get("XDG_CONFIG_HOME", str(Path.home() / ".config")))
    return root / APP_NAME


def default_game_dir() -> Path:
    return app_data_dir() / "minecraft"


def safe_name(value: str, limit: int = 72) -> str:
    """Sanitize an item name for Windows/macOS/Linux; never allow path traversal."""
    result = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", str(value)).strip(" .")
    result = result[:limit].rstrip(" .")
    if not result or result.split(".")[0].lower() in {"con", "prn", "aux", "nul"} | {f"com{i}" for i in range(1, 10)} | {f"lpt{i}" for i in range(1, 10)}:
        result = "download"
    return result


def safe_filename(name: str) -> str:
    # Reject path segments, rather than silently accepting archive filenames.
    if not name or Path(name).name != name or "\\" in name or name in (".", ".."):
        raise ValueError("不安全的文件名，已拒绝下载")
    output = safe_name(name, 180)
    if output != name or output.startswith("."):
        raise ValueError("文件名含不安全字符，已拒绝下载")
    return output


def _hash_matches(path: Path, hashes: dict[str, str] | None) -> bool:
    hashes = hashes or {}
    for algorithm in ("sha512", "sha256", "sha1"):
        digest = hashes.get(algorithm)
        if digest:
            actual = _hash_file(path, algorithm)
            return actual.lower() == digest.lower()
    return True


def _hash_file(path: Path, algorithm: str) -> str:
    h = hashlib.new(algorithm)
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def mirror_url(url: str) -> str:
    """Rewrite known Mojang download URLs only; do not mirror unknown domains."""
    parsed = urlparse(url)
    if parsed.scheme != "https":
        return url
    mappings = {
        "piston-meta.mojang.com": "",
        "launchermeta.mojang.com": "",
        "launcher.mojang.com": "",
        "piston-data.mojang.com": "",
        "libraries.minecraft.net": "/maven",
        "resources.download.minecraft.net": "/assets",
        "maven.fabricmc.net": "/maven",
        "meta.fabricmc.net": "/fabric-meta",
    }
    prefix = mappings.get(parsed.hostname or "")
    if prefix is None:
        return url
    path = parsed.path
    # Only mirror documented Maven and metadata paths. Never rewrite other services.
    # The mirror's version manifest lives at /mc/game even if upstream used piston-meta.
    return "https://bmclapi2.bangbang93.com" + prefix + path + ("?" + parsed.query if parsed.query else "")


@contextmanager
def minecraft_network(mirror: bool, proxy: str = ""):
    """Temporarily route minecraft-launcher-lib requests through BMCLAPI.

    This patches requests within a *serialized installation task* only. HTTPS
    certificate checks stay enabled. Unknown endpoints remain unchanged.
    """
    original_request = requests.sessions.Session.request
    old_http, old_https = os.environ.get("HTTP_PROXY"), os.environ.get("HTTPS_PROXY")
    if proxy.strip():
        os.environ["HTTP_PROXY"] = proxy.strip()
        os.environ["HTTPS_PROXY"] = proxy.strip()

    def request_with_mirror(self, method, url, **kwargs):
        # A stalled mirror should not hang a multi-file install indefinitely.
        # Explicit per-call timeouts from minecraft-launcher-lib take precedence.
        kwargs.setdefault("timeout", (7, 45))
        target = mirror_url(str(url)) if mirror else str(url)
        candidates = [target, str(url)] if target != str(url) else [str(url)]
        last_exc = None
        for candidate in candidates:
            for attempt in range(2):
                try:
                    response = original_request(self, method, candidate, **kwargs)
                    if candidate != str(url) and response.status_code >= 400:
                        response.close()
                        break
                    return response
                except (requests.Timeout, requests.ConnectionError) as error:
                    last_exc = error
                    if attempt == 0:
                        time.sleep(0.25)
            # Fail over to the source after mirror failures.
        if last_exc:
            raise last_exc
        raise RuntimeError("下载源请求失败")

    requests.sessions.Session.request = request_with_mirror
    try:
        yield
    finally:
        requests.sessions.Session.request = original_request
        for key, previous in (("HTTP_PROXY", old_http), ("HTTPS_PROXY", old_https)):
            if previous is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = previous


@contextmanager
def minecraft_download_parallelism(mc: Any, workers: int):
    """Limit Minecraft libraries/assets/JVM downloads to a user-configured pool.

    minecraft-launcher-lib 8 provides max_workers on its lower-level
    download functions, even though install_minecraft_version has no such
    parameter. Patch only these functions for the duration of ONE installation;
    don't monkeypatch the global ThreadPoolExecutor used by unrelated tasks.
    Versions with different signatures are left untouched.
    """
    count = max(2, min(32, int(workers)))
    module = mc.install
    originals = []

    def install_override(owner: Any, name: str) -> None:
        func = getattr(owner, name, None)
        if not callable(func):
            return
        try:
            signature = inspect.signature(func)
        except (TypeError, ValueError):
            return
        if "max_workers" not in signature.parameters:
            return
        @functools.wraps(func)
        def limited(*args, **kwargs):
            bound = signature.bind_partial(*args, **kwargs)
            if "max_workers" not in bound.arguments:
                kwargs["max_workers"] = count
            return func(*args, **kwargs)
        originals.append((owner, name, func))
        setattr(owner, name, limited)

    install_override(module, "install_libraries")
    install_override(module, "install_assets")
    # install.py imports install_jvm_runtime directly; changes here are picked
    # up by the module's do_version_install function.
    install_override(module, "install_jvm_runtime")
    try:
        yield
    finally:
        for owner, name, original in reversed(originals):
            setattr(owner, name, original)


class Config:
    def __init__(self, path: Path | None = None):
        self.path = Path(path or app_data_dir() / "settings.json")
        self.data: dict[str, Any] = {
            "root": str(default_game_dir()),
            "mirror": True,
            "download_threads": 12,
            "proxy": "",
            "java": "",
            "memory_mb": 4096,
            "width": 1280,
            "height": 720,
            "background": "",
            "language": "auto",
            "client_id": "",
            "redirect_uri": "http://localhost",
            "account_name": "",
            "account_id": "",
            "auth_flow": "browser",
            "account_mode": "microsoft",
            "offline_name": "",
            "profiles": [],
        }
        if self.path.exists():
            try:
                loaded = json.loads(self.path.read_text(encoding="utf-8"))
                if isinstance(loaded, dict):
                    self.data.update({k: v for k, v in loaded.items() if k in self.data})
            except (OSError, ValueError):
                pass

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(self.data, ensure_ascii=False, indent=2), encoding="utf-8")
        tmp.replace(self.path)

    @property
    def root(self) -> Path:
        return Path(self.data["root"]).expanduser().resolve()

    def add_profile(self, label: str, base_version: str, launch_version: str,
                    loader: str = "vanilla", instance_dir: Path | None = None) -> dict:
        profile = {
            "id": uuid.uuid4().hex[:12], "name": str(label), "base_version": str(base_version),
            "launch_version": str(launch_version), "loader": loader,
            "dir": str(instance_dir or (self.root / "instances" / uuid.uuid4().hex)),
        }
        self.data["profiles"].append(profile)
        self.save()
        return profile

    def remove_profile(self, profile_id: str) -> None:
        # Never delete saves or game directories implicitly.
        self.data["profiles"] = [p for p in self.data["profiles"] if p["id"] != profile_id]
        self.save()


def offline_account(username: str) -> dict[str, Any]:
    """Create a local-only launch identity without any OAuth tokens.

    Vanilla Java offline UUIDs are version-3 (MD5) name UUIDs for the
    UTF-8 text ``OfflinePlayer:<username>``. Keeping this deterministic
    avoids a new UUID (and thus losing player inventory) on each launch.
    These identities are not authenticated and are not usable for Realms
    or servers that enforce Microsoft account authentication.
    """
    if not isinstance(username, str) or not re.fullmatch(r"[A-Za-z0-9_]{3,16}", username):
        raise ValueError("离线玩家名称必须为 3～16 位英文字母、数字或下划线")
    player_uuid = uuid.UUID(bytes=hashlib.md5(("OfflinePlayer:" + username).encode("utf-8")).digest(), version=3)
    return {"name": username, "id": player_uuid.hex, "access_token": "0", "offline": True}


def make_callback(report: Progress) -> dict:
    state = {"current": 0, "max": 100}

    def set_status(message):
        report(str(message), state["current"], state["max"])

    def set_progress(value):
        state["current"] = int(value)
        report("正在下载 / 安装文件", state["current"], state["max"])

    def set_max(value):
        state["max"] = max(1, int(value))
        report("正在准备下载", state["current"], state["max"])

    return {"setStatus": set_status, "setProgress": set_progress, "setMax": set_max}


def _mc():
    try:
        import minecraft_launcher_lib
        return minecraft_launcher_lib
    except ImportError as e:
        raise RuntimeError("缺少 minecraft-launcher-lib，请运行启动脚本安装依赖") from e


class MinecraftService:
    def __init__(self, config: Config):
        self.config = config

    def versions(self) -> list[dict]:
        urls = [MIRROR_MANIFEST, OFFICIAL_MANIFEST] if self.config.data["mirror"] else [OFFICIAL_MANIFEST, MIRROR_MANIFEST]
        cache = self.config.path.parent / "version_manifest_cache.json"
        errors = []
        for url in urls:
            try:
                response = requests.get(url, headers={"User-Agent": USER_AGENT}, timeout=18,
                                        proxies=self._proxies())
                response.raise_for_status()
                data = response.json()
                if not isinstance(data.get("versions"), list):
                    raise ValueError("服务器返回的版本列表无效")
                cache.parent.mkdir(parents=True, exist_ok=True)
                cache.write_text(json.dumps(data), encoding="utf-8")
                return data["versions"]
            except (requests.RequestException, ValueError, OSError) as exc:
                errors.append(str(exc))
        if cache.exists():
            return json.loads(cache.read_text(encoding="utf-8"))["versions"]
        raise RuntimeError("无法获取官方版本列表：" + "; ".join(errors[:2]))

    def _proxies(self) -> dict | None:
        value = self.config.data.get("proxy", "").strip()
        return {"http": value, "https": value} if value else None

    def installed(self) -> list[dict]:
        root = self.config.root / "versions"
        if not root.exists():
            return []
        return [{"id": folder.name, "type": "installed"} for folder in root.iterdir()
                if folder.is_dir() and (folder / (folder.name + ".json")).is_file()]

    def install(self, version: str, loader: str, name: str, report: Progress = _noop) -> dict:
        if not version or any(c in version for c in "\\/:"):
            raise ValueError("无效的游戏版本")
        if loader not in ("vanilla", "fabric", "forge", "neoforge", "quilt"):
            raise ValueError("未知模组加载器")
        mc = _mc()
        root = self.config.root
        root.mkdir(parents=True, exist_ok=True)
        report(f"下载连接数：{max(2, min(32, int(self.config.data.get('download_threads', 12))))}；"
               f"Minecraft 下载源：{'BMCLAPI 优先' if self.config.data['mirror'] else '官方优先'}", None, None)
        with minecraft_network(bool(self.config.data["mirror"]), self.config.data["proxy"]), \
             minecraft_download_parallelism(mc, self.config.data.get("download_threads", 12)):
            if loader == "vanilla":
                mc.install.install_minecraft_version(version, str(root), callback=make_callback(report))
                launch_version = version
            else:
                report("正在检查加载器兼容性", None, None)
                selected = mc.mod_loader.get_mod_loader(loader)
                if not selected.is_minecraft_version_supported(version):
                    raise ValueError(f"{loader} 不支持 Minecraft {version}")
                launch_version = selected.install(version, str(root), callback=make_callback(report),
                                                   java=self.config.data["java"] or None)
        profile = self.config.add_profile(name or f"Minecraft {version} ({loader})", version, launch_version, loader)
        Path(profile["dir"]).mkdir(parents=True, exist_ok=True)
        return profile

    def install_mrpack(self, path: Path, name: str, report: Progress = _noop) -> dict:
        mc = _mc()
        path = Path(path).resolve()
        if path.suffix.lower() != ".mrpack" or not path.is_file():
            raise ValueError("请选择有效的 .mrpack 文件")
        info = mc.mrpack.get_mrpack_information(str(path))
        game_version = str(info["minecraftVersion"])
        launch_version = mc.mrpack.get_mrpack_launch_version(str(path))
        instance = self.config.root / "instances" / uuid.uuid4().hex
        instance.mkdir(parents=True, exist_ok=True)
        try:
            with minecraft_network(bool(self.config.data["mirror"]), self.config.data["proxy"]), \
                 minecraft_download_parallelism(mc, self.config.data.get("download_threads", 12)):
                mc.mrpack.install_mrpack(str(path), str(self.config.root), modpack_directory=str(instance),
                                         callback=make_callback(report))
        except Exception:
            # Preserve files for diagnosis; don't register a broken profile.
            raise
        return self.config.add_profile(name or info.get("name", "整合包"), game_version,
                                       launch_version, "modpack", instance)

    def launch(self, profile: dict, account: dict, report: Progress = _noop) -> int:
        mc = _mc()
        root = self.config.root
        instance = Path(profile["dir"])
        instance.mkdir(parents=True, exist_ok=True)
        if not (root / "versions" / profile["launch_version"] / (profile["launch_version"] + ".json")).is_file():
            raise RuntimeError("当前实例缺少版本文件，请重新安装版本")
        if account.get("offline"):
            # Do not trust caller-supplied offline UUIDs or credentials.
            account = offline_account(account.get("name", ""))
        elif not account.get("access_token") or not account.get("id"):
            raise RuntimeError("请先登录拥有 Minecraft Java 版的 Microsoft 账号")
        mem = max(1024, min(65536, int(self.config.data["memory_mb"])))
        opts = {
            "username": account["name"], "uuid": account["id"], "token": account["access_token"],
            "gameDirectory": str(instance), "jvmArguments": [f"-Xmx{mem}M"],
            "launcherName": APP_NAME, "launcherVersion": APP_VERSION,
            "customResolution": True,
            "resolutionWidth": str(self.config.data["width"]),
            "resolutionHeight": str(self.config.data["height"]),
        }
        if str(self.config.data.get("java", "")).strip():
            java = Path(self.config.data["java"]).expanduser()
            if not java.is_file():
                raise FileNotFoundError(f"Java 路径不存在：{java}")
            opts["executablePath"] = str(java)
        command = mc.command.get_minecraft_command(profile["launch_version"], str(root), opts)
        log_dir = self.config.path.parent / "logs"
        log_dir.mkdir(parents=True, exist_ok=True)
        log_path = log_dir / (time.strftime("game-%Y%m%d-%H%M%S") + ".log")
        report(f"启动中：{profile['name']}", None, None)
        # Never put access tokens, launch command or account credentials into app logs.
        with log_path.open("w", encoding="utf-8", errors="replace") as log:
            proc = subprocess.Popen(command, cwd=str(instance), stdout=log, stderr=subprocess.STDOUT,
                                    creationflags=(subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0))
        return proc.pid


class BrowserAuthReceiver:
    """One-shot OAuth callback receiver bound ONLY to IPv4 loopback.

    Nothing in this server records or exposes authorization codes in logs.
    The OAuth state is checked before a callback is accepted. The login API
    performs the separate PKCE verifier check when redeeming the code.
    """

    def __init__(self, configured_redirect: str):
        parsed = urlsplit(configured_redirect)
        if (parsed.scheme != "http" or parsed.hostname not in ("localhost", "127.0.0.1")
                or parsed.username or parsed.password or parsed.query or parsed.fragment):
            raise ValueError("网页自动登录需要设置本机 Redirect URI，建议 http://localhost（在 Azure 注册为桌面应用）")
        # Microsoft allows arbitrary loopback ports when 'localhost' is used.
        # For a literal 127.0.0.1 URI, preserve its explicitly registered port.
        if parsed.hostname == "127.0.0.1" and parsed.port is None:
            raise ValueError("127.0.0.1 回调地址需要填写 Azure 中注册的端口，或改用 http://localhost")
        port = (parsed.port if parsed.hostname == "127.0.0.1" else 0)
        route = parsed.path or "/"
        self.state = ""
        self._redirect_url: str | None = None
        self._error: str | None = None
        self._finished = threading.Event()
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, format, *args):
                # BaseHTTPRequestHandler normally logs the URL with the OAuth
                # code in its query string. Never emit that sensitive data.
                return

            def do_GET(self):
                query_url = urlsplit(self.path)
                expected_host = f"{parsed.hostname}:{owner.server.server_port}"
                if self.headers.get("Host", "").lower() != expected_host.lower():
                    self._reply(400, "无效的回调地址。")
                    return
                if query_url.path != route or owner._finished.is_set():
                    self._reply(404, "此登录请求已经结束或地址不正确。")
                    return
                params = parse_qs(query_url.query, keep_blank_values=True)
                states = params.get("state", [])
                if len(states) != 1 or not owner.state or not hmac.compare_digest(states[0], owner.state):
                    self._reply(400, "登录状态校验失败，请返回启动器重试。")
                    return
                if "error" in params:
                    owner._error = "Microsoft 网页登录已取消或被拒绝。"
                elif len(params.get("code", [])) == 1 and params["code"][0]:
                    # Preserve the exact generated redirect_uri rather than
                    # trusting user-controlled Host or absolute request URL.
                    owner._redirect_url = owner.redirect_uri + "?" + query_url.query
                else:
                    self._reply(400, "回调缺少授权码。")
                    return
                self._reply(200, "已收到 Microsoft 登录结果，可以关闭此页面并返回启动器。")
                owner._finished.set()

            def _reply(self, status: int, message: str):
                from html import escape
                body = ("<!doctype html><html lang='zh-CN'><meta charset='utf-8'>"
                        "<title>BlockCraft Launcher</title>"
                        "<body style='font:16px system-ui;margin:12vh auto;max-width:560px'>"
                        "<h2>BlockCraft Launcher</h2><p>" + escape(message) + "</p></body></html>").encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Cache-Control", "no-store")
                self.send_header("Referrer-Policy", "no-referrer")
                self.send_header("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'")
                self.send_header("X-Content-Type-Options", "nosniff")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                try:
                    self.wfile.write(body)
                except (BrokenPipeError, ConnectionResetError):
                    pass

        try:
            self.server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
        except OSError as exc:
            raise RuntimeError("无法监听本机登录回调端口，请检查端口占用或安全软件") from exc
        self.server.daemon_threads = True
        self.server.timeout = 0.2
        self.redirect_uri = urlunsplit(("http", f"{parsed.hostname}:{self.server.server_port}", route, "", ""))
        self.thread: threading.Thread | None = None

    def begin(self, expected_state: str):
        self.state = expected_state
        self.thread = threading.Thread(target=self.server.serve_forever,
                                       kwargs={"poll_interval": 0.1},
                                       daemon=True, name="blockcraft-oauth-loopback")
        self.thread.start()

    def wait(self, timeout: float = 240.0) -> str:
        if not self._finished.wait(timeout):
            raise TimeoutError("网页授权等待超时，请重新点击登录")
        if self._error:
            raise RuntimeError(self._error)
        if not self._redirect_url:
            raise RuntimeError("未收到有效的网页登录结果")
        return self._redirect_url

    def cancel(self):
        if not self._finished.is_set():
            self._error = "登录已取消"
            self._finished.set()
        self.close()

    def close(self):
        if self.thread is not None and self.thread.is_alive():
            self.server.shutdown()
        self.server.server_close()


class MicrosoftLogin:
    """Browser OAuth sign-in with PKCE, state, and loopback callback capture.

    The Microsoft password never goes through the launcher. Refresh tokens are
    stored only in Windows Credential Manager/keyring, never the settings JSON.
    """
    KEYRING_SERVICE = "BlockCraftLauncher.Microsoft"

    def __init__(self, config: Config):
        self.config = config
        self.current: dict[str, Any] = {}
        self.pending: tuple[str, str, str] | None = None
        self._fallback_refresh: str | None = None
        self._browser_receiver: BrowserAuthReceiver | None = None
        self._device_cancel = threading.Event()
        self._device_flow: dict[str, Any] | None = None

    def start_browser(self) -> str:
        """Open an isolated localhost callback port and return the auth URL."""
        self.cancel_browser()
        self.cancel_device()
        client_id, configured = self._app_credentials()
        receiver = BrowserAuthReceiver(configured)
        try:
            login_url, state, verifier = _mc().microsoft_account.get_secure_login_data(
                client_id, receiver.redirect_uri)
            parsed_url = urlsplit(login_url)
            if parsed_url.scheme != "https" or parsed_url.hostname not in (
                "login.microsoftonline.com", "login.live.com"
            ):
                raise RuntimeError("微软授权网址未通过安全校验，已取消登录")
            self.pending = (state, verifier, receiver.redirect_uri)
            receiver.begin(state)
            self._browser_receiver = receiver
            return login_url
        except Exception:
            receiver.close()
            self.pending = None
            raise

    def await_browser_login(self, timeout: float = 240.0) -> dict:
        """Call on a worker thread; never block Tk's main event loop."""
        receiver = self._browser_receiver
        if receiver is None:
            raise RuntimeError("没有正在进行的网页授权")
        try:
            url = receiver.wait(timeout)
            return self.finish(url)
        finally:
            receiver.close()
            if self._browser_receiver is receiver:
                self._browser_receiver = None
                self.pending = None

    def cancel_browser(self):
        receiver = self._browser_receiver
        if receiver is not None:
            self._browser_receiver = None
            receiver.cancel()
        self.pending = None

    def start(self) -> str:
        """Legacy manual callback flow, retained for advanced/custom redirects."""
        self.cancel_browser()
        cid, redirect = self._app_credentials()
        login_url, state, verifier = _mc().microsoft_account.get_secure_login_data(cid, redirect)
        self.pending = (state, verifier, redirect)
        return login_url

    def finish(self, redirected_url: str) -> dict:
        if self.pending is None:
            raise RuntimeError("请先点击「网页登录 Microsoft」")
        cid, _ = self._app_credentials()
        state, verifier, redirect = self.pending
        try:
            code = _mc().microsoft_account.parse_auth_code_url(redirected_url.strip(), state)
        except Exception:
            raise RuntimeError("Microsoft 授权验证失败（state 不匹配或授权码无效）") from None
        try:
            with minecraft_network(False, self.config.data["proxy"]):
                info = _mc().microsoft_account.complete_login(cid, None, redirect, code, verifier)
        except Exception as exc:
            # Do not display library messages/URLs: they might contain auth codes.
            raise RuntimeError(f"Microsoft/Minecraft 登录校验未通过（{type(exc).__name__}），请检查 Azure 权限与网络") from None
        self.pending = None
        self.config.data["auth_flow"] = "browser"
        self._save_auth(info)
        return info

    def start_device(self) -> dict[str, Any]:
        """Begin the Microsoft consumer device-code grant; never store device secrets."""
        self.cancel_browser()
        self.cancel_device()
        client_id, _ = self._app_credentials()
        response = requests.post(
            "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            data={"client_id": client_id, "scope": "XboxLive.signin offline_access"},
            headers={"User-Agent": USER_AGENT, "Accept": "application/json"},
            timeout=(10, 25), proxies=self._proxies())
        try:
            payload = response.json()
        except ValueError:
            raise RuntimeError("微软设备代码服务返回了无法识别的响应") from None
        if response.status_code != 200 or "device_code" not in payload:
            raise RuntimeError("无法获取设备登录代码，请检查微软授权和网络连接")
        if not all(isinstance(payload.get(field), str) and payload[field]
                   for field in ("device_code", "user_code", "verification_uri")):
            raise RuntimeError("微软设备代码响应缺少必要字段")
        uri = payload["verification_uri"]
        parsed = urlsplit(uri)
        if parsed.scheme != "https" or parsed.hostname not in (
                "microsoft.com", "www.microsoft.com", "login.microsoftonline.com"):
            raise RuntimeError("微软设备登录网址未通过安全校验")
        interval = max(5, min(60, int(payload.get("interval", 5))))
        expires_in = max(1, min(1800, int(payload.get("expires_in", 900))))
        self._device_cancel = threading.Event()
        self._device_flow = {
            "client_id": client_id, "device_code": payload["device_code"],
            "user_code": payload["user_code"], "verification_uri": uri,
            "interval": interval, "deadline": time.monotonic() + expires_in,
        }
        return {"user_code": payload["user_code"], "verification_uri": uri,
                "expires_in": expires_in}

    def await_device_login(self) -> dict:
        """Poll at the server's interval on a worker thread until complete/cancelled."""
        flow = self._device_flow
        if not flow:
            raise RuntimeError("没有正在进行的设备代码登录")
        cancel = self._device_cancel
        interval = flow["interval"]
        try:
            while time.monotonic() < flow["deadline"]:
                # Respect the advertised minimum interval before the first poll.
                if cancel.wait(interval):
                    raise RuntimeError("登录已取消")
                if time.monotonic() >= flow["deadline"]:
                    break
                try:
                    response = requests.post(
                        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                        data={"client_id": flow["client_id"],
                              "device_code": flow["device_code"],
                              "grant_type": "urn:ietf:params:oauth:grant-type:device_code"},
                        headers={"User-Agent": USER_AGENT, "Accept": "application/json"},
                        timeout=(10, 25), proxies=self._proxies())
                    payload = response.json()
                except (requests.RequestException, ValueError):
                    if cancel.is_set():
                        raise RuntimeError("登录已取消") from None
                    # Transient network errors must not cause a busy loop.
                    continue
                if cancel.is_set():
                    raise RuntimeError("登录已取消")
                if response.status_code == 200 and payload.get("access_token"):
                    info = self._finish_device_token(payload)
                    if cancel.is_set():
                        raise RuntimeError("登录已取消")
                    self.config.data["auth_flow"] = "device"
                    self._save_auth(info)
                    return info
                error = payload.get("error", "")
                if error == "authorization_pending":
                    continue
                if error == "slow_down":
                    interval = min(60, interval + 5)
                    continue
                if error == "authorization_declined":
                    raise RuntimeError("你已在微软网页拒绝授权")
                if error in ("expired_token", "bad_verification_code"):
                    raise RuntimeError("设备代码已失效，请重新获取登录代码")
                raise RuntimeError("Microsoft 设备代码授权失败，请检查应用权限或稍后重试")
            raise TimeoutError("设备代码已过期，请重新登录")
        finally:
            if self._device_flow is flow:
                self._device_flow = None

    def cancel_device(self) -> None:
        self._device_cancel.set()
        self._device_flow = None

    def _proxies(self) -> dict | None:
        proxy = str(self.config.data.get("proxy", "")).strip()
        return {"http": proxy, "https": proxy} if proxy else None

    def _finish_device_token(self, token: dict) -> dict:
        """Convert an MSA token to Minecraft credentials and verify ownership."""
        try:
            account = _mc().microsoft_account
            with minecraft_network(False, self.config.data["proxy"]):
                xbl = account.authenticate_with_xbl(token["access_token"])
                xsts = account.authenticate_with_xsts(xbl["Token"])
                userhash = xbl["DisplayClaims"]["xui"][0]["uhs"]
                minecraft = account.authenticate_with_minecraft(userhash, xsts["Token"])
                if "access_token" not in minecraft:
                    raise ValueError("Minecraft API permission denied")
                profile = account.get_profile(minecraft["access_token"])
                if "id" not in profile or "name" not in profile:
                    raise ValueError("Minecraft Java account not found")
            info = dict(profile)
            info["access_token"] = minecraft["access_token"]
            info["refresh_token"] = token.get("refresh_token", "")
            return info
        except Exception:
            # Never forward third-party errors, which can include sensitive tokens.
            raise RuntimeError("Minecraft 账号验证失败：请确认拥有 Java 版且应用获批访问 Minecraft API") from None

    def refresh(self) -> dict:
        cid, redirect = self._app_credentials()
        token = self._read_refresh()
        if not token:
            raise RuntimeError("请重新登录 Microsoft 账号")
        if self.config.data.get("auth_flow") == "device":
            try:
                response = requests.post(
                    "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
                    data={"client_id": cid, "scope": "XboxLive.signin offline_access",
                          "refresh_token": token, "grant_type": "refresh_token"},
                    headers={"User-Agent": USER_AGENT, "Accept": "application/json"},
                    timeout=(10, 25), proxies=self._proxies())
                payload = response.json()
                if response.status_code != 200 or not payload.get("access_token"):
                    raise RuntimeError("refresh denied")
                info = self._finish_device_token(payload)
            except Exception:
                raise RuntimeError("Microsoft 设备登录已过期，请重新登录") from None
        else:
            with minecraft_network(False, self.config.data["proxy"]):
                info = _mc().microsoft_account.complete_refresh(cid, None, redirect, token)
        self._save_auth(info)
        return info

    @staticmethod
    def _developer_client_id() -> str:
        """Find the launcher's developer-provisioned public app identity.

        The EXE can bundle auth_app.json as a PyInstaller data file. Source
        distributions can supply the same file next to core.py. Users should
        not need to apply for or enter an Entra application ID themselves.
        An app ID is *not* a password or a client secret.
        """
        cid = os.environ.get("BLOCKCRAFT_MS_CLIENT_ID", "").strip()
        if cid:
            return cid
        roots = [Path(__file__).resolve().parent]
        if getattr(sys, "frozen", False):
            roots.insert(0, Path(getattr(sys, "_MEIPASS", roots[0])))
        for root in roots:
            config_file = root / "auth_app.json"
            try:
                data = json.loads(config_file.read_text(encoding="utf-8"))
            except (OSError, ValueError):
                continue
            if isinstance(data, dict):
                cid = str(data.get("client_id", "")).strip()
                if cid:
                    return cid
        return ""

    def _app_credentials(self) -> tuple[str, str]:
        """Use the developer's app registration; never request a user password.

        Do not reuse a different launcher's Microsoft app identity. The
        legacy client_id setting remains readable for existing installations.
        """
        cid = self._developer_client_id() or str(self.config.data.get("client_id", "")).strip()
        if not cid:
            raise ValueError(
                "启动器维护者尚未预置获批的 Microsoft 应用身份，因此正版网页登录暂不可用。"
                "普通玩家不需要填写 Azure ID；请使用已配置合法应用身份的发布版，"
                "或暂时选择离线模式。"
            )
        # Desktop public-client system-browser sign-in uses the exact
        # registration redirect http://localhost and an ephemeral local port.
        uri = "http://localhost"
        return cid, uri

    def _save_auth(self, info: dict) -> None:
        self.current = info
        self.config.data["account_name"] = info["name"]
        self.config.data["account_id"] = info["id"]
        self.config.save()
        refresh_token = info.get("refresh_token", "")
        self._fallback_refresh = refresh_token
        try:
            import keyring
            keyring.set_password(self.KEYRING_SERVICE, "refresh", refresh_token)
        except Exception:
            # Password is in memory for the current process only; never write
            # sensitive credentials into the JSON configuration.
            pass

    def _read_refresh(self) -> str | None:
        if self._fallback_refresh:
            return self._fallback_refresh
        try:
            import keyring
            return keyring.get_password(self.KEYRING_SERVICE, "refresh")
        except Exception:
            return None

    def logout(self) -> None:
        self.cancel_browser()
        self.cancel_device()
        self.current = {}
        self._fallback_refresh = None
        try:
            import keyring
            keyring.delete_password(self.KEYRING_SERVICE, "refresh")
        except Exception:
            pass
        self.config.data["account_name"] = ""
        self.config.data["account_id"] = ""
        self.config.data["auth_flow"] = "browser"
        self.config.save()

class ModrinthService:
    def __init__(self, config: Config):
        self.config = config
        self.session = requests.Session()
        self.session.headers.update({"User-Agent": USER_AGENT, "Accept": "application/json"})

    def get(self, path: str, params: dict | None = None) -> Any:
        response = self.session.get(MODRINTH_API + path, params=params, timeout=(10, 30),
                                    proxies=self._proxies())
        response.raise_for_status()
        return response.json()

    def _proxies(self):
        value = self.config.data["proxy"].strip()
        return {"http": value, "https": value} if value else None

    def search_page(self, query: str, kind: str, version: str = "", loader: str = "",
                    category: str = "", sort: str = "relevance", page: int = 1,
                    limit: int = 20) -> dict:
        """Search Modrinth with AND-combined facets and server-side pagination.

        Only the public API is called; this does not need a personal API key.
        The actual list of versions supported by a project is verified at install time.
        """
        if kind not in (*MODRINTH_KINDS, "modpack"):
            raise ValueError("未知资源类型")
        if sort not in ("relevance", "downloads", "follows", "newest", "updated"):
            raise ValueError("未知排序方式")
        if not isinstance(page, int) or page < 1 or page > 10000:
            raise ValueError("无效页码")
        if not isinstance(limit, int) or not 1 <= limit <= 100:
            raise ValueError("每页数量必须在 1 到 100 之间")
        query = query.strip()[:256]
        facets = [[f"project_type:{kind}"]]
        for value, field in ((version.strip(), "versions"), (category.strip(), "categories")):
            if value:
                if not re.fullmatch(r"[a-zA-Z0-9_.-]{1,64}", value):
                    raise ValueError("版本或分类包含非法字符")
                facets.append([f"{field}:{value}"])
        if kind in ("mod", "modpack") and loader and loader not in ("vanilla", "modpack", "any"):
            if loader not in ("fabric", "forge", "neoforge", "quilt"):
                raise ValueError("未知模组加载器")
            facets.append([f"categories:{loader}"])
        offset = (page - 1) * limit
        result = self.get("/search", {"query": query, "facets": json.dumps(facets, ensure_ascii=False),
                                      "index": sort, "offset": offset, "limit": limit})
        if not isinstance(result, dict) or not isinstance(result.get("hits"), list):
            raise ValueError("Modrinth 返回了无效搜索数据")
        total = max(0, int(result.get("total_hits", len(result["hits"]))))
        return {"hits": result["hits"], "total_hits": total,
                "page": page, "limit": limit, "offset": offset}

    def search(self, query: str, kind: str, version: str = "", loader: str = "", limit: int = 30) -> list[dict]:
        """Legacy compatibility for earlier UI/tests."""
        return self.search_page(query, kind, version, loader, sort="downloads", limit=limit)["hits"]

    def project(self, project_id: str) -> dict:
        return self.get(f"/project/{quote(project_id, safe='')}")

    def project_versions(self, project_id: str, kind: str, game_version: str = "", loader: str = "") -> list[dict]:
        params = {}
        if game_version:
            params["game_versions"] = json.dumps([game_version])
        if kind in ("mod", "modpack") and loader and loader not in ("vanilla", "modpack", "any"):
            params["loaders"] = json.dumps([loader])
        return self.get(f"/project/{quote(project_id, safe='')}/version", params)

    @staticmethod
    def primary_file(version: dict, suffix: str | None = None) -> dict:
        files = version.get("files", [])
        candidates = [f for f in files if (not suffix or f.get("filename", "").lower().endswith(suffix))]
        if not candidates:
            raise ValueError("该版本没有可下载的兼容文件")
        return next((f for f in candidates if f.get("primary")), candidates[0])

    def download(self, file: dict, folder: Path, report: Progress = _noop) -> Path:
        filename = safe_filename(file.get("filename", ""))
        url = file.get("url", "")
        parsed = urlparse(url)
        if parsed.scheme != "https" or not parsed.netloc:
            raise ValueError("只支持可信 HTTPS 下载链接")
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / filename
        hashes = file.get("hashes") or {}
        if path.exists() and (not file.get("size") or path.stat().st_size == int(file["size"])) and _hash_matches(path, hashes):
            report(f"文件已存在：{filename}", 1, 1)
            return path
        part = path.with_name(path.name + ".part")
        for attempt in range(3):
            existing = part.stat().st_size if part.exists() else 0
            headers = {"Range": f"bytes={existing}-"} if existing else {}
            try:
                with self.session.get(url, headers=headers, stream=True, timeout=(12, 50),
                                      proxies=self._proxies()) as response:
                    if existing and response.status_code == 416:
                        part.unlink(missing_ok=True)
                        continue
                    response.raise_for_status()
                    # If server ignored Range, overwrite instead of appending duplicate data.
                    resumed = existing > 0 and response.status_code == 206
                    if not resumed:
                        existing = 0
                    length = int(response.headers.get("Content-Length") or 0)
                    total = int(file.get("size") or 0) or (existing + length if length else None)
                    with part.open("ab" if resumed else "wb") as out:
                        count = existing
                        for chunk in response.iter_content(chunk_size=128 * 1024):
                            if chunk:
                                out.write(chunk)
                                count += len(chunk)
                                report(f"下载 {filename}", count, total)
                if file.get("size") and part.stat().st_size != int(file["size"]):
                    raise ValueError("下载文件大小不匹配")
                if not _hash_matches(part, hashes):
                    part.unlink(missing_ok=True)
                    raise ValueError("SHA 校验失败，镜像文件不完整或已改变")
                part.replace(path)
                return path
            except (requests.RequestException, OSError, ValueError) as exc:
                if attempt >= 2:
                    raise RuntimeError(f"下载失败 {filename}: {exc}") from exc
                report(f"网络异常，正在重试 ({attempt + 1}/3)：{filename}", None, None)
                time.sleep(0.6 * (attempt + 1))
        raise RuntimeError("下载失败")

    def install_resource(self, project_id: str, kind: str, profile: dict,
                         report: Progress = _noop) -> list[Path]:
        if kind not in MODRINTH_KINDS:
            raise ValueError("请选择模组 / 光影 / 资源包")
        version = profile["base_version"]
        loader = profile["loader"]
        if kind == "mod" and loader in ("vanilla", "modpack"):
            if loader == "vanilla":
                raise ValueError("原版无法加载模组，请新建 Fabric / Forge / NeoForge / Quilt 实例")
            raise ValueError("请为整合包添加自定义模组时手动选择兼容文件")
        installed: list[Path] = []
        visited: set[str] = set()

        def install_one(pid: str, explicit_version_id: str | None = None, depth: int = 0):
            if depth > 18:
                raise RuntimeError("依赖层级过深")
            if pid in visited or (explicit_version_id and explicit_version_id in visited):
                return
            if explicit_version_id:
                v = self.get("/version/" + quote(explicit_version_id, safe=""))
                if version not in v.get("game_versions", []):
                    raise ValueError(f"依赖版本与 Minecraft {version} 不兼容")
                if loader not in v.get("loaders", []) and kind == "mod":
                    raise ValueError(f"依赖加载器与 {loader} 不兼容")
            else:
                matches = self.project_versions(pid, kind, version, loader)
                if not matches:
                    raise ValueError(f"资源 {pid} 没有兼容当前实例的版本")
                v = matches[0]
            visited.add(pid)
            visited.add(v["id"])
            target = Path(profile["dir"]) / MODRINTH_KINDS[kind]
            installed.append(self.download(self.primary_file(v), target, report))
            if kind == "mod":
                for dep in v.get("dependencies", []):
                    if dep.get("dependency_type") != "required":
                        continue
                    next_pid = dep.get("project_id") or ""
                    next_vid = dep.get("version_id")
                    if not next_pid and next_vid:
                        info = self.get("/version/" + quote(next_vid, safe=""))
                        next_pid = info.get("project_id", "")
                    if not next_pid:
                        raise RuntimeError("模组存在无法自动识别的必需依赖，请到项目页面检查")
                    install_one(next_pid, next_vid, depth + 1)

        install_one(project_id)
        return installed


def copy_local_resource(source: Path, kind: str, profile: dict) -> Path:
    if kind not in MODRINTH_KINDS:
        raise ValueError("无效资源类型")
    source = Path(source)
    suffix = source.suffix.lower()
    if (kind == "mod" and suffix != ".jar") or (kind != "mod" and suffix != ".zip"):
        raise ValueError("模组需要 .jar 文件，光影和资源包需要 .zip 文件")
    destination = Path(profile["dir"]) / MODRINTH_KINDS[kind]
    destination.mkdir(parents=True, exist_ok=True)
    target = destination / safe_filename(source.name)
    if source.resolve() != target.resolve():
        shutil.copy2(source, target)
    return target


# ============================================================================
# I18N: locale catalog and translations
# ============================================================================

import json
import locale
from pathlib import Path

LANGUAGES = {
    "auto": "🌐  Auto / 跟随系统语言",
    "zh_cn": "简体中文（中国）", "zh_tw": "繁體中文（臺灣）", "zh_hk": "繁體中文（香港）",
    "en_us": "English (United States)", "en_gb": "English (United Kingdom)",
    "en_au": "English (Australia)", "en_ca": "English (Canada)", "en_nz": "English (New Zealand)",
    "ja_jp": "日本語（日本）", "ko_kr": "한국어 (대한민국)", "fr_fr": "Français (France)",
    "fr_ca": "Français (Canada)", "fr_ch": "Français (Suisse)",
    "de_de": "Deutsch (Deutschland)", "de_at": "Deutsch (Österreich)", "de_ch": "Deutsch (Schweiz)",
    "es_es": "Español (España)", "es_mx": "Español (México)", "es_ar": "Español (Argentina)",
    "es_cl": "Español (Chile)", "es_ec": "Español (Ecuador)", "es_uy": "Español (Uruguay)",
    "es_ve": "Español (Venezuela)", "pt_br": "Português (Brasil)", "pt_pt": "Português (Portugal)",
    "ru_ru": "Русский", "uk_ua": "Українська", "ar_sa": "العربية", "hi_in": "हिन्दी",
    "it_it": "Italiano", "tr_tr": "Türkçe", "vi_vn": "Tiếng Việt", "id_id": "Bahasa Indonesia",
    "th_th": "ไทย", "pl_pl": "Polski", "nl_nl": "Nederlands", "nl_be": "Vlaams",
    "sv_se": "Svenska", "da_dk": "Dansk", "fi_fi": "Suomi", "no_no": "Norsk",
    "nn_no": "Nynorsk", "cs_cz": "Čeština", "sk_sk": "Slovenčina", "ro_ro": "Română",
    "hu_hu": "Magyar", "el_gr": "Ελληνικά", "he_il": "עברית", "fa_ir": "فارسی",
    "bg_bg": "Български", "hr_hr": "Hrvatski", "sr_sp": "Српски", "sl_si": "Slovenščina",
    "et_ee": "Eesti", "lv_lv": "Latviešu", "lt_lt": "Lietuvių", "ca_es": "Català",
    "eu_es": "Euskara", "gl_es": "Galego", "fil_ph": "Filipino", "ms_my": "Bahasa Melayu",
    "af_za": "Afrikaans", "ast_es": "Asturianu", "az_az": "Azərbaycanca", "ba_ru": "Башҡортса",
    "bar": "Boarisch", "be_by": "Беларуская", "br_fr": "Brezhoneg", "bs_ba": "Bosanski",
    "cy_gb": "Cymraeg", "dsb_de": "Dolnoserbšćina", "en_pt": "Pirate Speak",
    "en_ud": "Upside-down English", "enp": "Anglish", "enws": "Shakespearean English",
    "eo_uy": "Esperanto", "fo_fo": "Føroyskt", "fra_de": "Fränggisch",
    "fur_it": "Furlan", "fy_nl": "Frysk", "ga_ie": "Gaeilge", "gd_gb": "Gàidhlig",
    "got": "Gutrazda", "haw_us": "ʻŌlelo Hawaiʻi", "hsb_de": "Hornjoserbšćina",
    "hy_am": "Հայերեն", "ig_ng": "Igbo", "io_en": "Ido", "is_is": "Íslenska",
    "jbo_en": "la .lojban.", "ka_ge": "ქართული", "kk_kz": "Қазақша", "kn_in": "ಕನ್ನಡ",
    "ksh": "Kölsch", "kw_gb": "Kernewek", "ky_kg": "Кыргызча", "la_la": "Latina",
    "lb_lu": "Lëtzebuergesch", "li_li": "Limburgs", "lmo": "Lombard", "lo_la": "ລາວ",
    "lol_us": "LOLCAT", "mk_mk": "Македонски", "mn_mn": "Монгол", "mt_mt": "Malti",
    "nds_de": "Plattdüütsch", "oc_fr": "Occitan", "qya_aa": "Quenya",
    "se_no": "Davvisámegiella", "sq_al": "Shqip", "swg": "Schwäbisch", "sxu": "Sächsisch",
    "szl": "Ślōnskŏ gŏdka", "ta_in": "தமிழ்", "tl_ph": "Tagalog", "tlh_aa": "tlhIngan Hol",
    "tok": "toki pona", "tt_ru": "Татарча", "val_es": "Valencià", "vec_it": "Vèneto",
    "yi_de": "ייִדיש", "yo_ng": "Yorùbá", "zlm_arab": "بهاس ملايو", "uz_uz": "Oʻzbekcha",
    "cv_ru": "Чӑвашла", "zh_classical": "文言", "ry_ua": "Русиньскый", "scn": "Sicilianu",
    "ne_np": "नेपाली", "ur_pk": "اردو", "bn_bd": "বাংলা", "sw_ke": "Kiswahili",
}

# All source UI text falls back to English if the requested translation is absent.
EN = {
    "请搜索以查看结果": "Search to see results",
    "搜索结果已过期": "Search results are outdated",
    "资源类型已更改，请重新搜索后再安装。": "The resource type changed. Please search again before installing.",
    "搜索模组、光影与资源包 · 可先搜索后选择安装实例": "Search mods, shaders and resource packs · browse before choosing an instance",
    "整合包管理": "Modpack Manager",
    "Modrinth .mrpack 在线安装、本地导入与兼容版本筛选": "Find and install compatible Modrinth .mrpack modpacks",
    "全部加载器": "Any Loader",
    "游戏版本": "Minecraft Version",
    "分类": "Category",
    "全部分类": "All Categories",
    "排序": "Sort by",
    "相关度": "Relevance",
    "下载量": "Downloads",
    "关注数": "Followers",
    "最新发布": "Newest",
    "最近更新": "Recently Updated",
    "冒险": "Adventure",
    "装饰": "Decoration",
    "装备": "Equipment",
    "食物": "Food",
    "优化": "Optimization",
    "科技": "Technology",
    "魔法": "Magic",
    "世界生成": "World generation",
    "工具": "Utility",
    "生物": "Mobs",
    "存储": "Storage",
    "写实": "Realistic",
    "原版风格": "Vanilla-like",
    "幻想": "Fantasy",
    "轻量": "Lightweight",
    "半写实": "Semi-realistic",
    "卡通": "Cartoon",
    "简洁": "Simplistic",
    "主题": "Themed",
    "音效": "Audio",
    "战斗": "Combat",
    "任务": "Quests",
    "多人": "Multiplayer",
    "高难度": "Challenging",
    "支持版本": "Supported Versions",
    "第": "Page",
    "页": "page",
    "共": "Total",
    "条资源": "results",
    "上一页": "Previous",
    "下一页": "Next",
    "查看详情": "View Details",
    "获取资源详情": "Fetching Project Details",
    "可用版本": "Available versions",
    "关闭": "Close",
    "提示：搜索不要求安装实例；安装时按所选实例实际版本再次检查兼容性。": "Tip: You can browse without an instance. Installation checks compatibility with the selected instance.",
    "输入关键词后搜索，也可留空浏览热门项目": "Search by keyword, or leave it empty to browse popular projects",
    "输入关键词后搜索，也可留空浏览热门整合包": "Search by keyword, or browse modpacks without a keyword",
    "Python 运行环境（仅 Windows EXE 引导版）": "Python runtime (Windows EXE bootstrap only)",
    "每 7 天自动检查兼容的 Python 维护更新；升级前会询问你。": "Checks for compatible Python maintenance updates every 7 days; asks before installing.",
    "下次启动立即检查 Python 更新": "Check Python updates on next launch",
    "Python 更新": "Python update",
    "此功能只适用于 Windows EXE 引导版。": "This feature is only available in the Windows EXE bootstrap version.",
    "已安排：下次打开 Windows EXE 时会检查 Python 更新。": "Scheduled: Python updates will be checked on the next Windows EXE launch.",
    "微软正版网页登录": "Sign in with Microsoft (browser)",
    "在微软官方网页输入账号密码或使用通行密钥；启动器不会读取密码。授权后自动返回。":
        "Enter your password or passkey on Microsoft's website. The launcher never reads your password; you'll return automatically.",

    "尚未登录 Microsoft": "Not signed in to Microsoft", "就绪 · 欢迎使用 BlockCraft Launcher": "Ready · Welcome to BlockCraft Launcher",
    "功能导航": "Navigation", "首页": "Home", "游戏版本": "Game Versions", "资源中心": "Resource Center",
    "整合包": "Modpacks", "设置": "Settings", "运行日志": "Logs", "账户": "Account",
    "登录 / 切换账号": "Sign in / Switch account", "欢迎回到方块世界": "Welcome to the block world",
    "选择一个实例，构建你的下一段冒险。": "Choose an instance and start your next adventure.",
    "我的游戏实例": "My Game Instances", "打开游戏文件夹": "Open Game Folder",
    "每个实例的存档、模组、光影和资源包独立保存。": "Each instance keeps saves, mods, shaders and resource packs separate.",
    "▶  启动游戏": "▶  Play", "+  安装新版本": "+  Install Version", "移除实例记录": "Remove Instance",
    "快速开始": "Quick Start", "① 在「游戏版本」中安装原版或加载器。\n② 在「资源中心」安装模组、光影与资源包。\n③ 登录持有 Java 版的微软账号后启动。":
        "① Install the game or a mod loader under Game Versions.\n② Install mods, shaders and resource packs in Resource Center.\n③ Sign in with a Microsoft account that owns Java Edition.",
    "创造 · 探索 · 无限可能": "Create · Explore · Endless Possibilities",
    "全版本 · 模组管理 · 自定义外观": "All versions · Mods · Custom appearance",
    "安装 Minecraft": "Install Minecraft", "来自 Mojang 官方版本列表 · 可选 Fabric / Forge / NeoForge / Quilt":
        "Official Mojang version list · Fabric / Forge / NeoForge / Quilt available",
    "全部版本": "All Versions", "正式版": "Releases", "快照版": "Snapshots", "远古 Beta": "Old Beta", "远古 Alpha": "Old Alpha",
    "刷新列表": "Refresh List", "版本号": "Version", "类型": "Type", "发布日期": "Release Date",
    "加载器": "Loader", "安装选中版本": "Install Selected Version", "可双击版本进行安装": "Double-click a version to install",
    "Modrinth 资源中心": "Modrinth Resource Center", "搜索并安装与当前实例兼容的模组、光影、资源包":
        "Find and install compatible mods, shaders and resource packs",
    "目标实例": "Target Instance", "模组": "Mods", "光影": "Shaders", "资源包": "Resource Packs",
    "搜索资源": "Search Resources", "资源名称": "Name", "作者": "Author", "下载次数": "Downloads",
    "下载并安装选中资源": "Download & Install Selected", "导入本地文件": "Import Local File",
    "查看项目主页": "View Project Page", "整合包管理": "Modpack Manager",
    "Modrinth .mrpack 在线安装和本地导入 · 自动创建独立实例":
        "Install or import Modrinth .mrpack files · Creates separate instances",
    "搜索整合包": "Search Modpacks", "下载量": "Downloads", "一键安装选中整合包": "Install Selected Modpack",
    "从本地 .mrpack 导入": "Import Local .mrpack", "打开整合包网页": "Open Modpack Website",
    "说明：部分整合包依赖外部下载或作者授权，无法保证全部自动安装。":
        "Note: Some modpacks need external files or author permission, so automatic installation is not guaranteed.",
    "启动器设置": "Launcher Settings", "网络加速、Java 运行时、账户认证和自定义背景":
        "Network, Java runtime, account authentication and custom backgrounds",
    "Minecraft 文件目录": "Minecraft Directory", "Java 可执行文件（留空自动选择）": "Java Executable (empty = automatic)",
    "HTTP / HTTPS 代理（可留空）": "HTTP / HTTPS Proxy (optional)", "Azure 应用 Client ID": "Azure Application Client ID",
    "Microsoft Redirect URI": "Microsoft Redirect URI", "自定义背景图片（PNG / JPG）": "Custom Background (PNG / JPG)",
    "浏览": "Browse", "优先使用 BMCLAPI 国内镜像，失败回退官方":
        "Prefer BMCLAPI mirror, fall back to official servers",
    "游戏最大内存（MB）": "Maximum Game Memory (MB)", "保存设置": "Save Settings",
    "正版登录需要具备 Minecraft API 权限的 Azure 应用注册。密钥不写入配置文件。":
        "Official login requires an Azure app permitted to use the Minecraft API. Secrets are not stored in settings.",
    "安装过程、错误与下载状态（不记录账号令牌）":
        "Installation progress, errors and download status (account tokens are not logged)",
    "清空显示": "Clear Display", "任务进行中": "Task in Progress", "请等待当前任务完成后再操作。":
        "Please wait for the current task to finish.",
    "完成": "Completed", "完成：": "Completed: ", "失败": "Failed", "失败：": "failed: ",
    "操作失败": "Operation Failed", "\n\n请检查网络、版本兼容性和设置。":
        "\n\nCheck your connection, version compatibility and settings.",
    "界面更新失败：": "Could not update UI: ", "获取游戏版本列表": "Fetching Game Versions",
    "版本列表已加载，共 ": "Loaded version list: ", " 个版本。": " versions.",
    "选择版本": "Choose Version", "请先选中一个 Minecraft 版本。": "Select a Minecraft version first.",
    "安装新实例": "Install New Instance", "准备安装：\nMinecraft ": "Ready to install:\nMinecraft ",
    "\n加载器：": "\nLoader: ", "\n\n要继续吗？": "\n\nContinue?",
    "新实例：": "New instance: ", "没有实例": "No Instance", "请先在「游戏版本」中安装或导入一个实例":
        "Install or import an instance under Game Versions first.",
    "无法打开": "Cannot Open", "确认移除": "Confirm Removal", "只从启动器列表移除「":
        "Remove “", "」吗？\n不会删除存档或目录。": "” from the launcher list?\nSave files and directories will be kept.",
    "无法启动": "Cannot Launch", "请先登录 Microsoft 正版账号": "Sign in with your Microsoft account first",
    "启动 Minecraft": "Launching Minecraft", "游戏已启动": "Game Started",
    "已提交启动进程（PID: ": "Game process started (PID: ", "）。\n游戏日志存于启动器数据目录。":
        ").\nGame logs are stored in the launcher's data folder.",
    "需要配置 Microsoft 登录": "Microsoft Login Requires Configuration", "Microsoft 正版登录": "Microsoft Sign-in",
    "在浏览器完成 Microsoft 登录": "Complete Microsoft Sign-in in Your Browser",
    "登录后，将浏览器跳转到的完整地址粘贴到下面。仅用于读取一次性授权码。":
        "After signing in, paste the complete redirect URL below to read the one-time authorization code.",
    "再次打开浏览器": "Open Browser Again", "输入地址": "Enter URL", "请粘贴浏览器跳转后的完整网址。":
        "Paste the complete redirected URL from your browser.",
    "完成登录": "Finish Sign-in", "退出当前账号": "Sign Out", "登录成功": "Signed In",
    "当前账号：": "Current account: ", "选择实例": "Choose Instance", "搜索 Modrinth 资源": "Search Modrinth Resources",
    "没有结果": "No Results", "没有找到兼容的资源。可更换关键词或 Minecraft 版本。":
        "No compatible resources found. Try another search or Minecraft version.",
    "请先选择一个搜索结果": "Select a search result first", "请选择资源": "Select Resource",
    "下载并安装资源": "Downloading & Installing Resource", "安装完成": "Installation Complete",
    "已安装文件：\n": "Installed files:\n", "选择资源文件": "Choose Resource File",
    "ZIP 资源文件": "ZIP Resource File", "所有文件": "All Files", "已导入": "Imported",
    "导入失败": "Import Failed", "选择资源": "Choose Resource", "搜索 Modrinth 整合包": "Search Modrinth Modpacks",
    "没有搜索到可用的整合包。": "No modpacks found.", "选择整合包": "Choose Modpack",
    "安装整合包": "Install Modpack", "即将下载整合包及其依赖文件。安装过程可能较长，是否继续？":
        "This will download the modpack and its dependencies. It may take a while. Continue?",
    "这个整合包没有可用版本": "This modpack has no available versions",
    "已创建：": "Created: ", "选择 Modrinth 整合包": "Choose Modrinth Modpack",
    "导入本地整合包": "Import Local Modpack", "已导入：": "Imported: ",
    "选择 Minecraft 安装目录": "Choose Minecraft Directory", "选择启动器背景图": "Choose Launcher Background",
    "图片": "Image", "选择 Java 可执行文件": "Choose Java Executable",
    "设置已保存": "Settings Saved", "设置已保存（Minecraft 目录与登录配置对后续操作生效）":
        "Settings saved (Minecraft path and login settings apply to future operations)",
    "修改已保存。背景会立即更新。\n下载设置仅影响后续下载任务。":
        "Changes saved. Background updates immediately.\nDownload options apply to future downloads.",
    "任务仍在进行": "Task Still Running", "当前有下载或安装任务，退出会中断操作。确定退出吗？":
        "A download or installation is running. Exit and interrupt the task?",
    "语言 / Language": "Language / 语言", "跟随系统": "Follow System", "翻译不完整时显示英文":
        "Missing translations are shown in English", "语言已切换": "Language changed",
}

EN.update({
    "网页登录 / 切换账号": "Web Sign-in / Switch account",
    "Microsoft 网页登录": "Microsoft Web Sign-in",
    "请在微软官方网页完成验证，成功后将自动返回启动器，无须复制网址。":
        "Sign in on the official Microsoft website. The launcher will receive the result automatically; no URL copying.",
    "等待网页授权（最多 4 分钟）。如浏览器没有自动打开，请点击下方按钮。":
        "Waiting for browser authentication (up to 4 minutes). If it did not open, click below.",
    "重新打开浏览器": "Reopen Browser",
    "取消登录": "Cancel Sign-in",
    "网页登录已取消": "Web sign-in cancelled",
    "浏览器未打开": "Browser Did Not Open",
    "请点击“重新打开浏览器”，在默认浏览器中登录。":
        "Select Reopen Browser and sign in using your default browser.",
    "网页登录使用系统浏览器和本地回调，不需要复制网址。仍需有 Minecraft API 权限的 Azure Client ID。":
        "Browser sign-in returns via localhost; no copy/paste. A Minecraft API-permitted Azure Client ID is still required.",
})

# Fully offline translations for key navigation, actions and settings. Additional
# UI text falls back to the complete English source dictionary above.
KEYS = [
    "首页", "游戏版本", "资源中心", "整合包", "设置", "运行日志", "账户", "登录 / 切换账号",
    "欢迎回到方块世界", "我的游戏实例", "打开游戏文件夹", "▶  启动游戏",
    "+  安装新版本", "移除实例记录", "快速开始", "安装 Minecraft", "全部版本",
    "正式版", "快照版", "刷新列表", "版本号", "类型", "发布日期", "加载器",
    "安装选中版本", "目标实例", "模组", "光影", "资源包", "搜索资源", "资源名称",
    "作者", "下载次数", "下载并安装选中资源", "导入本地文件", "查看项目主页",
    "整合包管理", "搜索整合包", "下载量", "一键安装选中整合包",
    "从本地 .mrpack 导入", "打开整合包网页", "启动器设置", "Minecraft 文件目录",
    "浏览", "游戏最大内存（MB）", "保存设置", "清空显示", "语言 / Language",
]

# Each ordered block corresponds to KEYS. Keeping key phrases in this compact form
# makes it straightforward to add or correct human translations without touching UI.
TRANSLATION_ROWS = {
    "zh_tw": "首頁|遊戲版本|資源中心|模組包|設定|執行紀錄|帳戶|登入 / 切換帳戶|歡迎回到方塊世界|我的遊戲實例|開啟遊戲資料夾|▶  啟動遊戲|+  安裝新版本|移除實例記錄|快速開始|安裝 Minecraft|所有版本|正式版|快照版|重新整理清單|版本號|類型|發行日期|載入器|安裝選取版本|目標實例|模組|光影|資源包|搜尋資源|資源名稱|作者|下載次數|下載並安裝選取資源|匯入本機檔案|檢視專案首頁|模組包管理|搜尋模組包|下載量|一鍵安裝選取模組包|從本機 .mrpack 匯入|開啟模組包網頁|啟動器設定|Minecraft 檔案目錄|瀏覽|遊戲最大記憶體（MB）|儲存設定|清除顯示|語言 / Language",
    "ja_jp": "ホーム|ゲームバージョン|リソースセンター|Modパック|設定|ログ|アカウント|ログイン / アカウント切替|ブロックの世界へようこそ|ゲームインスタンス|ゲームフォルダーを開く|▶  ゲームを起動|+  新しいバージョンを導入|インスタンスを削除|クイックスタート|Minecraft をインストール|すべてのバージョン|正式版|スナップショット|一覧を更新|バージョン|種類|公開日|ローダー|選択したバージョンを導入|対象インスタンス|Mod|シェーダー|リソースパック|リソースを検索|リソース名|作者|ダウンロード数|選択したリソースを導入|ローカルファイルを読み込む|プロジェクトページ|Modパック管理|Modパックを検索|ダウンロード数|選択したModパックを導入|ローカル .mrpack を読み込む|Modパックのサイト|ランチャー設定|Minecraft ディレクトリ|参照|最大メモリ（MB）|設定を保存|表示を消去|言語 / Language",
    "ko_kr": "홈|게임 버전|리소스 센터|모드팩|설정|로그|계정|로그인 / 계정 전환|블록 세계에 오신 것을 환영합니다|내 게임 인스턴스|게임 폴더 열기|▶  게임 실행|+  새 버전 설치|인스턴스 제거|빠른 시작|Minecraft 설치|모든 버전|정식 버전|스냅샷|목록 새로고침|버전|유형|출시일|로더|선택한 버전 설치|대상 인스턴스|모드|셰이더|리소스 팩|리소스 검색|리소스 이름|제작자|다운로드 수|선택 항목 다운로드 및 설치|로컬 파일 가져오기|프로젝트 페이지 보기|모드팩 관리|모드팩 검색|다운로드 수|선택한 모드팩 설치|로컬 .mrpack 가져오기|모드팩 웹사이트|런처 설정|Minecraft 폴더|찾아보기|최대 게임 메모리（MB）|설정 저장|표시 지우기|언어 / Language",
    "fr_fr": "Accueil|Versions du jeu|Centre de ressources|Modpacks|Paramètres|Journaux|Compte|Connexion / Changer de compte|Bienvenue dans le monde des blocs|Mes instances|Ouvrir le dossier du jeu|▶  Jouer|+  Installer une version|Retirer l’instance|Démarrage rapide|Installer Minecraft|Toutes les versions|Versions stables|Snapshots|Actualiser la liste|Version|Type|Date de sortie|Chargeur|Installer la version choisie|Instance cible|Mods|Shaders|Packs de ressources|Rechercher|Nom de la ressource|Auteur|Téléchargements|Télécharger et installer|Importer un fichier local|Voir le projet|Gestion des modpacks|Rechercher des modpacks|Téléchargements|Installer le modpack choisi|Importer .mrpack local|Site du modpack|Paramètres du lanceur|Dossier Minecraft|Parcourir|Mémoire maximale (Mo)|Enregistrer|Effacer|Langue / Language",
    "de_de": "Startseite|Spielversionen|Ressourcen-Center|Modpacks|Einstellungen|Protokolle|Konto|Anmelden / Konto wechseln|Willkommen in der Blockwelt|Meine Spielinstanzen|Spielordner öffnen|▶  Spielen|+  Version installieren|Instanz entfernen|Schnellstart|Minecraft installieren|Alle Versionen|Veröffentlichungen|Schnappschüsse|Liste aktualisieren|Version|Typ|Veröffentlichungsdatum|Loader|Ausgewählte Version installieren|Zielinstanz|Mods|Shader|Ressourcenpakete|Ressourcen suchen|Ressourcenname|Autor|Downloads|Ausgewählte Ressource installieren|Lokale Datei importieren|Projektseite öffnen|Modpack-Verwaltung|Modpacks suchen|Downloads|Modpack installieren|Lokale .mrpack importieren|Modpack-Webseite öffnen|Launcher-Einstellungen|Minecraft-Verzeichnis|Durchsuchen|Maximaler Arbeitsspeicher (MB)|Einstellungen speichern|Anzeige leeren|Sprache / Language",
    "es_es": "Inicio|Versiones|Centro de recursos|Paquetes de mods|Ajustes|Registros|Cuenta|Iniciar sesión / Cambiar cuenta|Bienvenido al mundo de bloques|Mis instancias|Abrir carpeta del juego|▶  Jugar|+  Instalar versión|Quitar instancia|Inicio rápido|Instalar Minecraft|Todas las versiones|Versiones estables|Instantáneas|Actualizar lista|Versión|Tipo|Fecha de lanzamiento|Cargador|Instalar versión seleccionada|Instancia de destino|Mods|Shaders|Paquetes de recursos|Buscar recursos|Nombre|Autor|Descargas|Descargar e instalar|Importar archivo local|Ver página del proyecto|Gestión de modpacks|Buscar modpacks|Descargas|Instalar modpack seleccionado|Importar .mrpack local|Abrir web del modpack|Ajustes del lanzador|Directorio de Minecraft|Examinar|Memoria máxima (MB)|Guardar ajustes|Limpiar vista|Idioma / Language",
    "pt_br": "Início|Versões do jogo|Central de recursos|Modpacks|Configurações|Logs|Conta|Entrar / Trocar de conta|Bem-vindo ao mundo dos blocos|Minhas instâncias|Abrir pasta do jogo|▶  Jogar|+  Instalar versão|Remover instância|Início rápido|Instalar Minecraft|Todas as versões|Versões estáveis|Snapshots|Atualizar lista|Versão|Tipo|Data de lançamento|Carregador|Instalar versão selecionada|Instância de destino|Mods|Shaders|Pacotes de recursos|Pesquisar recursos|Nome do recurso|Autor|Downloads|Baixar e instalar|Importar arquivo local|Ver página do projeto|Gerenciar modpacks|Pesquisar modpacks|Downloads|Instalar modpack selecionado|Importar .mrpack local|Abrir site do modpack|Configurações do launcher|Pasta do Minecraft|Procurar|Memória máxima (MB)|Salvar configurações|Limpar tela|Idioma / Language",
    "ru_ru": "Главная|Версии игры|Центр ресурсов|Сборки|Настройки|Журнал|Аккаунт|Войти / Сменить аккаунт|Добро пожаловать в мир блоков|Мои сборки|Открыть папку игры|▶  Играть|+  Установить версию|Удалить запись|Быстрый старт|Установить Minecraft|Все версии|Релизы|Снимки|Обновить список|Версия|Тип|Дата выпуска|Загрузчик|Установить выбранную версию|Целевая сборка|Моды|Шейдеры|Наборы ресурсов|Поиск ресурсов|Название|Автор|Загрузки|Скачать и установить|Импортировать файл|Открыть страницу проекта|Менеджер сборок|Поиск сборок|Загрузки|Установить сборку|Импортировать .mrpack|Сайт сборки|Настройки лаунчера|Папка Minecraft|Обзор|Максимум памяти (МБ)|Сохранить настройки|Очистить|Язык / Language",
    "it_it": "Home|Versioni di gioco|Centro risorse|Modpack|Impostazioni|Registro|Account|Accedi / Cambia account|Benvenuto nel mondo dei blocchi|Le mie istanze|Apri cartella del gioco|▶  Gioca|+  Installa versione|Rimuovi istanza|Avvio rapido|Installa Minecraft|Tutte le versioni|Versioni stabili|Snapshot|Aggiorna elenco|Versione|Tipo|Data di rilascio|Loader|Installa versione selezionata|Istanza di destinazione|Mod|Shader|Pacchetti risorse|Cerca risorse|Nome risorsa|Autore|Download|Scarica e installa|Importa file locale|Pagina del progetto|Gestione modpack|Cerca modpack|Download|Installa modpack scelto|Importa .mrpack locale|Sito del modpack|Impostazioni launcher|Cartella Minecraft|Sfoglia|Memoria massima (MB)|Salva impostazioni|Pulisci|Lingua / Language",
    "tr_tr": "Ana Sayfa|Oyun Sürümleri|Kaynak Merkezi|Mod Paketleri|Ayarlar|Günlükler|Hesap|Giriş / Hesap Değiştir|Blok dünyasına hoş geldin|Oyun örneklerim|Oyun klasörünü aç|▶  Oyna|+  Sürüm yükle|Örneği kaldır|Hızlı başlangıç|Minecraft'ı yükle|Tüm sürümler|Kararlı sürümler|Anlık görüntüler|Listeyi yenile|Sürüm|Tür|Yayın tarihi|Yükleyici|Seçili sürümü yükle|Hedef örnek|Modlar|Gölgelendiriciler|Kaynak paketleri|Kaynak ara|Kaynak adı|Yazar|İndirmeler|Seçileni indir ve yükle|Yerel dosya içe aktar|Proje sayfasını aç|Mod paketi yöneticisi|Mod paketi ara|İndirmeler|Seçili paketi yükle|Yerel .mrpack içe aktar|Paket web sitesi|Başlatıcı ayarları|Minecraft klasörü|Gözat|Maksimum bellek (MB)|Ayarları kaydet|Görünümü temizle|Dil / Language",
    "vi_vn": "Trang chủ|Phiên bản trò chơi|Trung tâm tài nguyên|Gói mod|Cài đặt|Nhật ký|Tài khoản|Đăng nhập / Đổi tài khoản|Chào mừng đến thế giới khối|Phiên bản đã cài|Mở thư mục trò chơi|▶  Chơi|+  Cài phiên bản mới|Xóa phiên bản|Bắt đầu nhanh|Cài đặt Minecraft|Tất cả phiên bản|Bản chính thức|Snapshot|Làm mới danh sách|Phiên bản|Loại|Ngày phát hành|Bộ nạp|Cài phiên bản đã chọn|Phiên bản đích|Mod|Shader|Gói tài nguyên|Tìm tài nguyên|Tên tài nguyên|Tác giả|Lượt tải|Tải và cài đặt|Nhập tệp cục bộ|Xem trang dự án|Quản lý gói mod|Tìm gói mod|Lượt tải|Cài gói mod đã chọn|Nhập .mrpack|Mở trang web|Cài đặt trình khởi chạy|Thư mục Minecraft|Duyệt|Bộ nhớ tối đa (MB)|Lưu cài đặt|Xóa hiển thị|Ngôn ngữ / Language",
    "id_id": "Beranda|Versi Gim|Pusat Sumber Daya|Modpack|Pengaturan|Log|Akun|Masuk / Ganti Akun|Selamat datang di dunia blok|Instansi Gim Saya|Buka Folder Gim|▶  Main|+  Pasang Versi|Hapus Instansi|Mulai Cepat|Pasang Minecraft|Semua Versi|Rilis|Snapshot|Segarkan Daftar|Versi|Jenis|Tanggal Rilis|Loader|Pasang Versi Terpilih|Instansi Tujuan|Mod|Shader|Paket Sumber Daya|Cari Sumber Daya|Nama Sumber Daya|Pembuat|Unduhan|Unduh dan Pasang|Impor File Lokal|Lihat Halaman Proyek|Kelola Modpack|Cari Modpack|Unduhan|Pasang Modpack|Impor .mrpack Lokal|Buka Situs Modpack|Pengaturan Peluncur|Folder Minecraft|Telusuri|Memori Maksimum (MB)|Simpan Pengaturan|Bersihkan Tampilan|Bahasa / Language",
    "pl_pl": "Strona główna|Wersje gry|Centrum zasobów|Paczki modów|Ustawienia|Dziennik|Konto|Zaloguj / Zmień konto|Witaj w świecie bloków|Moje instancje|Otwórz folder gry|▶  Graj|+  Zainstaluj wersję|Usuń instancję|Szybki start|Zainstaluj Minecraft|Wszystkie wersje|Wydania|Snapshoty|Odśwież listę|Wersja|Typ|Data wydania|Loader|Zainstaluj wybraną wersję|Instancja docelowa|Mody|Shadery|Paczki zasobów|Szukaj zasobów|Nazwa zasobu|Autor|Pobrania|Pobierz i zainstaluj|Importuj plik|Strona projektu|Zarządzaj modpackami|Szukaj paczek|Pobrania|Zainstaluj modpack|Importuj .mrpack|Otwórz stronę modpacka|Ustawienia launchera|Folder Minecraft|Przeglądaj|Maksymalna pamięć (MB)|Zapisz ustawienia|Wyczyść|Język / Language",
    "nl_nl": "Start|Spelversies|Bronnencentrum|Modpacks|Instellingen|Logboeken|Account|Aanmelden / Wisselen|Welkom in de blokkenwereld|Mijn instanties|Spelmap openen|▶  Spelen|+  Versie installeren|Instantie verwijderen|Snelstart|Minecraft installeren|Alle versies|Uitgaven|Snapshots|Lijst vernieuwen|Versie|Type|Releasedatum|Loader|Geselecteerde versie installeren|Doelinstantie|Mods|Shaders|Resourcepacks|Zoek bronnen|Naam|Auteur|Downloads|Downloaden en installeren|Lokaal bestand importeren|Projectpagina|Modpacks beheren|Modpacks zoeken|Downloads|Modpack installeren|Lokaal .mrpack importeren|Modpack-website|Launcher-instellingen|Minecraft-map|Bladeren|Maximaal geheugen (MB)|Instellingen opslaan|Wissen|Taal / Language",
    "th_th": "หน้าแรก|เวอร์ชันเกม|ศูนย์ทรัพยากร|ม็อดแพ็ก|ตั้งค่า|บันทึก|บัญชี|เข้าสู่ระบบ / เปลี่ยนบัญชี|ยินดีต้อนรับสู่โลกบล็อก|อินสแตนซ์ของฉัน|เปิดโฟลเดอร์เกม|▶  เล่นเกม|+  ติดตั้งเวอร์ชัน|ลบอินสแตนซ์|เริ่มต้นด่วน|ติดตั้ง Minecraft|ทุกเวอร์ชัน|รุ่นเต็ม|สแนปช็อต|รีเฟรช|เวอร์ชัน|ประเภท|วันที่เผยแพร่|ตัวโหลด|ติดตั้งเวอร์ชันที่เลือก|อินสแตนซ์เป้าหมาย|ม็อด|เชดเดอร์|แพ็กทรัพยากร|ค้นหา|ชื่อทรัพยากร|ผู้สร้าง|ดาวน์โหลด|ดาวน์โหลดและติดตั้ง|นำเข้าไฟล์|ดูหน้าโครงการ|จัดการม็อดแพ็ก|ค้นหาม็อดแพ็ก|ดาวน์โหลด|ติดตั้งม็อดแพ็ก|นำเข้า .mrpack|เปิดเว็บไซต์|ตั้งค่าตัวเปิดเกม|โฟลเดอร์ Minecraft|เรียกดู|หน่วยความจำสูงสุด (MB)|บันทึกการตั้งค่า|ล้างข้อมูล|ภาษา / Language",
    "uk_ua": "Головна|Версії гри|Центр ресурсів|Збірки|Налаштування|Журнал|Обліковий запис|Увійти / Змінити акаунт|Ласкаво просимо у світ блоків|Мої збірки|Відкрити теку гри|▶  Грати|+  Встановити версію|Прибрати збірку|Швидкий старт|Встановити Minecraft|Усі версії|Релізи|Знімки|Оновити список|Версія|Тип|Дата випуску|Завантажувач|Встановити вибрану версію|Цільова збірка|Моди|Шейдери|Набори ресурсів|Пошук ресурсів|Назва|Автор|Завантаження|Завантажити й встановити|Імпортувати файл|Сторінка проєкту|Керування збірками|Пошук збірок|Завантаження|Встановити збірку|Імпортувати .mrpack|Сайт збірки|Налаштування лаунчера|Тека Minecraft|Огляд|Максимальна пам'ять (МБ)|Зберегти налаштування|Очистити|Мова / Language",
}

# Locales which share a close base translation. If no language family exists,
# the complete English fallback is used.
FAMILY = {
    "zh_hk": "zh_tw", "fr_ca": "fr_fr", "fr_ch": "fr_fr", "de_at": "de_de", "de_ch": "de_de",
    "es_ar": "es_es", "es_cl": "es_es", "es_ec": "es_es", "es_mx": "es_es", "es_uy": "es_es", "es_ve": "es_es",
    "pt_pt": "pt_br", "nl_be": "nl_nl",
}

TRANSLATIONS = {}
for _locale, _row in TRANSLATION_ROWS.items():
    _values = _row.split("|")
    if len(_values) != len(KEYS):
        raise ValueError(f"Broken translation pack {_locale}: {len(_values)} of {len(KEYS)} entries")
    TRANSLATIONS[_locale] = dict(zip(KEYS, _values))


def system_language() -> str:
    """Resolve the machine UI locale safely across Windows/Linux/macOS."""
    try:
        value = locale.getlocale()[0] or ""
    except (ValueError, TypeError):
        value = ""
    candidate = value.replace("-", "_").lower()
    if candidate in LANGUAGES:
        return candidate
    prefix = candidate.split("_")[0]
    preferred = {
        "zh": "zh_cn", "en": "en_us", "ja": "ja_jp", "ko": "ko_kr",
        "pt": "pt_br", "es": "es_es", "fr": "fr_fr", "de": "de_de",
        "ru": "ru_ru", "uk": "uk_ua", "it": "it_it", "tr": "tr_tr",
        "vi": "vi_vn", "id": "id_id", "nl": "nl_nl", "pl": "pl_pl",
        "ar": "ar_sa", "hi": "hi_in", "th": "th_th", "sv": "sv_se",
        "fi": "fi_fi", "da": "da_dk", "no": "no_no", "cs": "cs_cz",
    }
    return preferred.get(prefix, "en_us")


class Translator:
    def __init__(self, language: str = "auto", overrides_dir: str | Path | None = None):
        self.requested = language if language in LANGUAGES else "auto"
        self.overrides_dir = Path(overrides_dir) if overrides_dir else None
        self._overrides: dict[str, str] = {}
        self.set_language(self.requested)

    @property
    def active(self) -> str:
        return system_language() if self.requested == "auto" else self.requested

    def set_language(self, language: str):
        self.requested = language if language in LANGUAGES else "auto"
        self._overrides = {}
        if self.overrides_dir:
            path = self.overrides_dir / (self.active + ".json")
            if path.is_file() and path.stat().st_size < 1024 * 1024:
                try:
                    data = json.loads(path.read_text(encoding="utf-8"))
                    if isinstance(data, dict):
                        self._overrides = {k: v for k, v in data.items()
                                           if isinstance(k, str) and isinstance(v, str)}
                except (OSError, ValueError):
                    pass

    def __call__(self, source: str) -> str:
        if not isinstance(source, str) or not source:
            return source
        target = self.active
        if target == "zh_cn":
            return self._overrides.get(source, source)
        family = FAMILY.get(target, target)
        translations = {**TRANSLATIONS.get(family, {}), **AUTH_TRANSLATIONS.get(family, {})}
        def lookup(s: str) -> str:
            return self._overrides.get(s, translations.get(s, EN.get(s, s)))
        if source in self._overrides or source in translations or source in EN:
            return lookup(source)
        # Dynamic status/dialog strings contain Chinese literals mixed with
        # version IDs and file names. Translate only recognized source segments.
        result = source
        for key in sorted(set(EN) | set(translations) | set(self._overrides), key=len, reverse=True):
            if key in result:
                result = result.replace(key, lookup(key))
        return result


def display_language(code: str) -> str:
    return f"{LANGUAGES[code]}  [{code}]" if code != "auto" else LANGUAGES[code]

# Additional community-friendly language coverage for the most-used controls.
EXTRA_ROWS = {
    "ar_sa": "الرئيسية|إصدارات اللعبة|مركز الموارد|حزم التعديلات|الإعدادات|السجلات|الحساب|تسجيل الدخول / تغيير الحساب|مرحبًا بك في عالم المكعبات|نسخ اللعبة|فتح مجلد اللعبة|▶  تشغيل|+  تثبيت إصدار|إزالة النسخة|البدء السريع|تثبيت Minecraft|جميع الإصدارات|الإصدارات الرسمية|اللقطات|تحديث القائمة|الإصدار|النوع|تاريخ الإصدار|المحمّل|تثبيت الإصدار المحدد|النسخة المستهدفة|تعديلات|مظلات|حزم الموارد|البحث عن موارد|اسم المورد|المؤلف|التنزيلات|تنزيل وتثبيت|استيراد ملف محلي|عرض صفحة المشروع|إدارة حزم التعديلات|البحث عن حزم|التنزيلات|تثبيت الحزمة|استيراد .mrpack محلي|فتح موقع الحزمة|إعدادات المشغّل|مجلد Minecraft|استعراض|الذاكرة القصوى (MB)|حفظ الإعدادات|مسح العرض|اللغة / Language",
    "hi_in": "होम|गेम संस्करण|संसाधन केंद्र|मॉडपैक|सेटिंग्स|लॉग|खाता|साइन इन / खाता बदलें|ब्लॉक की दुनिया में आपका स्वागत है|मेरे गेम इंस्टेंस|गेम फ़ोल्डर खोलें|▶  खेलें|+  संस्करण इंस्टॉल करें|इंस्टेंस हटाएँ|त्वरित शुरुआत|Minecraft इंस्टॉल करें|सभी संस्करण|रिलीज़|स्नैपशॉट|सूची रीफ़्रेश|संस्करण|प्रकार|रिलीज़ तिथि|लोडर|चुना संस्करण इंस्टॉल करें|लक्ष्य इंस्टेंस|मॉड|शेडर|संसाधन पैक|संसाधन खोजें|संसाधन नाम|लेखक|डाउनलोड|डाउनलोड और इंस्टॉल|स्थानीय फ़ाइल आयात|प्रोजेक्ट पेज देखें|मॉडपैक प्रबंधक|मॉडपैक खोजें|डाउनलोड|चुना मॉडपैक इंस्टॉल|स्थानीय .mrpack आयात|मॉडपैक वेबसाइट|लॉन्चर सेटिंग्स|Minecraft फ़ोल्डर|ब्राउज़|अधिकतम मेमोरी (MB)|सेटिंग्स सहेजें|डिस्प्ले साफ़ करें|भाषा / Language",
    "sv_se": "Hem|Spelversioner|Resurscenter|Modpaket|Inställningar|Loggar|Konto|Logga in / Byt konto|Välkommen till blockvärlden|Mina spelinstanser|Öppna spelmappen|▶  Spela|+  Installera version|Ta bort instans|Snabbstart|Installera Minecraft|Alla versioner|Utgåvor|Ögonblicksbilder|Uppdatera lista|Version|Typ|Utgivningsdatum|Laddare|Installera vald version|Målinstans|Moddar|Shaders|Resurspaket|Sök resurser|Resursnamn|Skapare|Nedladdningar|Ladda ner och installera|Importera lokal fil|Visa projektsida|Hantera modpaket|Sök modpaket|Nedladdningar|Installera modpaket|Importera lokal .mrpack|Öppna webbplats|Inställningar för startprogram|Minecraft-mapp|Bläddra|Maximalt minne (MB)|Spara inställningar|Rensa|Språk / Language",
    "cs_cz": "Domů|Verze hry|Centrum zdrojů|Balíčky modů|Nastavení|Protokoly|Účet|Přihlásit / Změnit účet|Vítejte ve světě kostek|Moje instance|Otevřít složku hry|▶  Hrát|+  Nainstalovat verzi|Odebrat instanci|Rychlý start|Nainstalovat Minecraft|Všechny verze|Vydání|Snímky|Obnovit seznam|Verze|Typ|Datum vydání|Zavaděč|Nainstalovat vybranou verzi|Cílová instance|Mody|Shadery|Balíčky zdrojů|Hledat zdroje|Název zdroje|Autor|Stažení|Stáhnout a nainstalovat|Importovat soubor|Stránka projektu|Správa balíčků|Hledat balíčky|Stažení|Nainstalovat balíček|Importovat .mrpack|Web balíčku|Nastavení launcheru|Složka Minecraft|Procházet|Maximální paměť (MB)|Uložit nastavení|Vymazat|Jazyk / Language",
    "fi_fi": "Koti|Peliversiot|Resurssikeskus|Modipaketit|Asetukset|Lokit|Tili|Kirjaudu / Vaihda tiliä|Tervetuloa palikkamaailmaan|Peli-instanssit|Avaa pelikansio|▶  Pelaa|+  Asenna versio|Poista instanssi|Pika-aloitus|Asenna Minecraft|Kaikki versiot|Julkaisut|Kehitysversiot|Päivitä luettelo|Versio|Tyyppi|Julkaisupäivä|Lataaja|Asenna valittu versio|Kohdeinstanssi|Modit|Varjostimet|Resurssipaketit|Etsi resursseja|Resurssin nimi|Tekijä|Lataukset|Lataa ja asenna|Tuo paikallinen tiedosto|Projektin sivu|Modipakettien hallinta|Etsi modipaketteja|Lataukset|Asenna modipaketti|Tuo .mrpack|Modipaketin sivusto|Käynnistimen asetukset|Minecraft-kansio|Selaa|Enimmäismuisti (MB)|Tallenna asetukset|Tyhjennä|Kieli / Language",
    "da_dk": "Hjem|Spilversioner|Ressourcecenter|Modpakker|Indstillinger|Logfiler|Konto|Log ind / Skift konto|Velkommen til blokverdenen|Mine spilinstanser|Åbn spilmappe|▶  Spil|+  Installer version|Fjern instans|Hurtig start|Installer Minecraft|Alle versioner|Udgivelser|Snapshots|Opdater liste|Version|Type|Udgivelsesdato|Loader|Installer valgt version|Målinstans|Mods|Shaders|Ressourcepakker|Søg ressourcer|Ressourcenavn|Forfatter|Downloads|Download og installer|Importer lokal fil|Se projektside|Modpakkehåndtering|Søg modpakker|Downloads|Installer modpakke|Importer .mrpack|Modpakkens hjemmeside|Launcher-indstillinger|Minecraft-mappe|Gennemse|Maksimal hukommelse (MB)|Gem indstillinger|Ryd|Sprog / Language",
    "no_no": "Hjem|Spillversjoner|Ressurssenter|Modpakker|Innstillinger|Logger|Konto|Logg inn / Bytt konto|Velkommen til blokkverdenen|Mine spillinstanser|Åpne spillmappe|▶  Spill|+  Installer versjon|Fjern instans|Hurtigstart|Installer Minecraft|Alle versjoner|Utgivelser|Øyeblikksbilder|Oppdater liste|Versjon|Type|Utgivelsesdato|Laster|Installer valgt versjon|Målinstans|Modifikasjoner|Skyggeeffekter|Ressurspakker|Søk ressurser|Ressursnavn|Forfatter|Nedlastinger|Last ned og installer|Importer lokal fil|Se prosjektside|Modpakke-behandling|Søk modpakker|Nedlastinger|Installer modpakke|Importer .mrpack|Modpakkens nettside|Innstillinger for starter|Minecraft-mappe|Bla gjennom|Maksimalt minne (MB)|Lagre innstillinger|Tøm|Språk / Language",
    "el_gr": "Αρχική|Εκδόσεις παιχνιδιού|Κέντρο πόρων|Πακέτα mods|Ρυθμίσεις|Αρχεία καταγραφής|Λογαριασμός|Σύνδεση / Αλλαγή λογαριασμού|Καλώς ήρθατε στον κόσμο των κύβων|Οι εγκαταστάσεις μου|Άνοιγμα φακέλου παιχνιδιού|▶  Παίξε|+  Εγκατάσταση έκδοσης|Αφαίρεση εγκατάστασης|Γρήγορη εκκίνηση|Εγκατάσταση Minecraft|Όλες οι εκδόσεις|Επίσημες εκδόσεις|Στιγμιότυπα|Ανανέωση λίστας|Έκδοση|Τύπος|Ημερομηνία έκδοσης|Φορτωτής|Εγκατάσταση επιλεγμένης έκδοσης|Εγκατάσταση στόχος|Mods|Shaders|Πακέτα πόρων|Αναζήτηση πόρων|Όνομα πόρου|Δημιουργός|Λήψεις|Λήψη και εγκατάσταση|Εισαγωγή τοπικού αρχείου|Σελίδα έργου|Διαχείριση modpacks|Αναζήτηση modpacks|Λήψεις|Εγκατάσταση modpack|Εισαγωγή .mrpack|Ιστότοπος modpack|Ρυθμίσεις εκκινητή|Φάκελος Minecraft|Αναζήτηση|Μέγιστη μνήμη (MB)|Αποθήκευση ρυθμίσεων|Εκκαθάριση|Γλώσσα / Language",
    "ro_ro": "Acasă|Versiuni joc|Centru de resurse|Pachete de moduri|Setări|Jurnale|Cont|Autentificare / Schimbă contul|Bun venit în lumea blocurilor|Instanțele mele|Deschide folderul jocului|▶  Joacă|+  Instalează versiunea|Elimină instanța|Pornire rapidă|Instalează Minecraft|Toate versiunile|Versiuni stabile|Snapshot-uri|Actualizează lista|Versiune|Tip|Data lansării|Încărcător|Instalează versiunea selectată|Instanță țintă|Moduri|Shadere|Pachete de resurse|Caută resurse|Nume resursă|Autor|Descărcări|Descarcă și instalează|Importă fișier local|Pagina proiectului|Gestionare modpack-uri|Caută modpack-uri|Descărcări|Instalează modpack|Importă .mrpack|Site-ul modpack-ului|Setări launcher|Folder Minecraft|Răsfoiește|Memorie maximă (MB)|Salvează setările|Curăță afișarea|Limbă / Language",
    "hu_hu": "Kezdőlap|Játékverziók|Erőforrásközpont|Modcsomagok|Beállítások|Naplók|Fiók|Bejelentkezés / Fiókváltás|Üdv a kockák világában|Játékpéldányaim|Játékmappa megnyitása|▶  Játék|+  Verzió telepítése|Példány eltávolítása|Gyors kezdés|Minecraft telepítése|Összes verzió|Kiadások|Pillanatképek|Lista frissítése|Verzió|Típus|Kiadási dátum|Betöltő|Kijelölt verzió telepítése|Célpéldány|Modok|Árnyalók|Erőforráscsomagok|Erőforrás keresése|Erőforrás neve|Szerző|Letöltések|Letöltés és telepítés|Helyi fájl importálása|Projektoldal|Modcsomag-kezelő|Modcsomag keresése|Letöltések|Modcsomag telepítése|.mrpack importálása|Modcsomag weboldala|Indító beállításai|Minecraft mappa|Tallózás|Maximális memória (MB)|Beállítások mentése|Törlés|Nyelv / Language",
    "he_il": "בית|גרסאות משחק|מרכז משאבים|חבילות מודים|הגדרות|יומנים|חשבון|התחברות / החלפת חשבון|ברוכים הבאים לעולם הבלוקים|מופעי המשחק שלי|פתיחת תיקיית המשחק|▶  שחק|+  התקנת גרסה|הסרת מופע|התחלה מהירה|התקנת Minecraft|כל הגרסאות|גרסאות רשמיות|תצלומי מצב|רענון רשימה|גרסה|סוג|תאריך יציאה|טוען|התקנת הגרסה שנבחרה|מופע יעד|מודים|שיידרים|חבילות משאבים|חיפוש משאבים|שם המשאב|יוצר|הורדות|הורדה והתקנה|ייבוא קובץ מקומי|דף הפרויקט|ניהול חבילות מודים|חיפוש חבילות|הורדות|התקנת חבילה|ייבוא .mrpack|אתר החבילה|הגדרות המשגר|תיקיית Minecraft|עיון|זיכרון מרבי (MB)|שמירת הגדרות|ניקוי תצוגה|שפה / Language",
}
for _locale, _row in EXTRA_ROWS.items():
    _values = _row.split("|")
    if len(_values) != len(KEYS):
        raise ValueError(f"Broken extra translation pack {_locale}: {len(_values)} of {len(KEYS)} entries")
    TRANSLATIONS[_locale] = dict(zip(KEYS, _values))

FAMILY.update({"nn_no": "no_no"})
EN.update({
    "原版": "Vanilla", "Minecraft Mod": "Minecraft Mod", "Java": "Java", "Modrinth 整合包": "Modrinth Modpack",
    "远古 Beta": "Old Beta", "远古 Alpha": "Old Alpha",
    "语言已切换": "Language changed",
})

# Login-method UI introduced in v0.3.1. Other locales inherit these English
# messages until translators provide native strings.
EN.update({
    "正版登录 / 切换账号": "Official Sign-in / Switch Account",
    "选择登录方式": "Choose a Sign-in Method",
    "设备代码：在微软网页输入显示的代码。网页：在电脑浏览器直接授权。":
        "Device code: enter the shown code on Microsoft's site. Browser: sign in directly on your computer.",
    "设备代码登录（推荐）": "Device Code Sign-in (Recommended)",
    "浏览器网页登录": "Browser Sign-in",
    "设备代码登录": "Device Code Sign-in",
    "Microsoft 设备代码登录": "Microsoft Device Code Sign-in",
    "在微软官方网页输入以下一次性代码（不要发送给其他人）：":
        "Enter this one-time code on Microsoft's official website. Do not share it.",
    "复制代码": "Copy Code",
    "打开微软验证网页": "Open Microsoft Verification Page",
    "取消登录": "Cancel Sign-in",
    "获取设备登录代码": "Getting Device Code",
    "Microsoft 登录不可用": "Microsoft Sign-in Unavailable",
    "浏览器未打开": "Browser Did Not Open",
    "请点击“打开微软验证网页”后输入上面的代码。":
        "Click Open Microsoft Verification Page and enter the code shown above.",
    "登录方式：设备代码或微软官方网页；普通用户无需填写应用 ID。":
        "Sign in with a device code or browser. End users do not need to enter an application ID.",
})
AUTH_TRANSLATIONS = {}
AUTH_TRANSLATIONS.setdefault("zh_tw", {}).update({
    "正版登录 / 切换账号": "正版登入 / 切換帳戶",
    "选择登录方式": "選擇登入方式",
    "设备代码登录（推荐）": "裝置代碼登入（推薦）",
    "浏览器网页登录": "瀏覽器網頁登入",
    "设备代码登录": "裝置代碼登入",
    "Microsoft 设备代码登录": "Microsoft 裝置代碼登入",
    "复制代码": "複製代碼",
    "打开微软验证网页": "開啟 Microsoft 驗證網頁",
    "取消登录": "取消登入",
})
AUTH_TRANSLATIONS.setdefault("ja_jp", {}).update({
    "正版登录 / 切换账号": "Microsoft ログイン / アカウント切替",
    "选择登录方式": "ログイン方法を選択",
    "设备代码登录（推荐）": "デバイスコードでログイン（推奨）",
    "浏览器网页登录": "ブラウザーでログイン",
    "设备代码登录": "デバイスコードログイン",
    "Microsoft 设备代码登录": "Microsoft デバイスコードログイン",
    "复制代码": "コードをコピー",
    "打开微软验证网页": "Microsoft 認証ページを開く",
    "取消登录": "ログインをキャンセル",
})
AUTH_TRANSLATIONS.setdefault("ko_kr", {}).update({
    "正版登录 / 切换账号": "Microsoft 로그인 / 계정 변경",
    "选择登录方式": "로그인 방식 선택",
    "设备代码登录（推荐）": "디바이스 코드 로그인 (권장)",
    "浏览器网页登录": "브라우저 로그인",
    "设备代码登录": "디바이스 코드 로그인",
    "复制代码": "코드 복사",
    "打开微软验证网页": "Microsoft 인증 페이지 열기",
    "取消登录": "로그인 취소",
})


# Local account / offline play added in v0.4.0. Localized strings for the main
# supported languages; other languages continue to use the English fallback.
EN.update({
    "账户 / 离线模式": "Accounts / Offline Mode",
    "选择游戏账户": "Select Game Account",
    "离线模式（无需账号）": "Offline Mode (No Account Needed)",
    "离线模式": "Offline Mode",
    "设置离线玩家名称": "Choose Offline Player Name",
    "离线模式：": "Offline: ",
    "未设置离线玩家名称": "Offline player name not set",
    "名称无效": "Invalid Name",
    "离线模式已启用": "Offline Mode Enabled",
    "启用离线模式": "Enable Offline Mode",
    "取消": "Cancel",
    "设备代码登录": "Device Code Sign-in",
    "离线模式不需要 Microsoft 登录，仅用于本地单人游戏和允许离线连接的服务器。":
        "Offline mode needs no Microsoft sign-in. For local single-player and servers permitting offline connections.",
    "Microsoft 正版账号：可使用设备代码或浏览器授权登录。":
        "Microsoft account: sign in with a device code or a browser.",
    "3～16 位英文字母、数字或下划线；同一名称会保留相同的本地玩家 UUID。":
        "3–16 Latin letters, numbers or underscores; the same name keeps the same local UUID.",
    "离线身份无法进入要求正版验证的服务器或 Realms。":
        "Offline profiles cannot join authenticated servers or Realms.",
    "请先选择离线模式或登录 Microsoft 正版账号":
        "Choose offline mode or sign in with your Microsoft account first",
    "③ 选择离线模式或使用 Microsoft 正版账号启动。":
        "③ Launch in offline mode or with an authenticated Microsoft account.",
})
for _locale, _items in {
    "zh_tw": {
        "账户 / 离线模式": "帳戶 / 離線模式", "选择游戏账户": "選擇遊戲帳戶",
        "离线模式（无需账号）": "離線模式（無需帳戶）", "离线模式": "離線模式",
        "设置离线玩家名称": "設定離線玩家名稱", "离线模式：": "離線模式：",
        "启用离线模式": "啟用離線模式", "离线模式已启用": "已啟用離線模式",
        "取消": "取消", "名称无效": "名稱無效",
    },
    "ja_jp": {
        "账户 / 离线模式": "アカウント / オフライン", "选择游戏账户": "ゲームアカウントを選択",
        "离线模式（无需账号）": "オフラインモード（アカウント不要）",
        "离线模式": "オフラインモード", "设置离线玩家名称": "オフラインプレイヤー名",
        "离线模式：": "オフライン：", "启用离线模式": "オフラインモードを有効にする",
        "离线模式已启用": "オフラインモードを有効にしました", "取消": "キャンセル",
    },
    "ko_kr": {
        "账户 / 离线模式": "계정 / 오프라인", "选择游戏账户": "게임 계정 선택",
        "离线模式（无需账号）": "오프라인 모드 (계정 불필요)",
        "离线模式": "오프라인 모드", "设置离线玩家名称": "오프라인 플레이어 이름",
        "离线模式：": "오프라인: ", "启用离线模式": "오프라인 모드 사용",
        "离线模式已启用": "오프라인 모드가 활성화됨", "取消": "취소",
    },
}.items():
    AUTH_TRANSLATIONS.setdefault(_locale, {}).update(_items)


# ============================================================================
# Desktop UI: tkinter
# ============================================================================

from datetime import datetime
import os
from pathlib import Path
import queue
import sys
import threading
import traceback
import webbrowser
import subprocess

import tkinter as tk
from tkinter import filedialog, messagebox, ttk, scrolledtext


try:
    from PIL import Image, ImageEnhance, ImageOps, ImageTk
except ImportError:
    Image = ImageEnhance = ImageOps = ImageTk = None

BG = "#0c101a"
SIDEBAR = "#101827"
PANEL = "#171f30"
PANEL2 = "#202b3e"
TEXT = "#eff5fd"
MUTED = "#95a5bc"
GREEN = "#58e2aa"
PURPLE = "#9473ff"
BORDER = "#2b364a"
UI_FONT = "Microsoft YaHei UI" if os.name == "nt" else "Noto Sans CJK SC"


def open_folder(path: Path):
    Path(path).mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        os.startfile(str(path))  # type: ignore[attr-defined]
    elif sys.platform == "darwin":
        subprocess.Popen(["open", str(path)])
    else:
        subprocess.Popen(["xdg-open", str(path)])


class LauncherApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.config_model = Config()
        self.i18n = Translator(self.config_model.data.get("language", "auto"),
                               self.config_model.path.parent / "lang")
        self.mc = MinecraftService(self.config_model)
        self.mods = ModrinthService(self.config_model)
        self.auth = MicrosoftLogin(self.config_model)
        self._login_window = None
        self._login_cancelled = False
        self.events: queue.Queue = queue.Queue()
        self.busy = False
        self.all_versions: list[dict] = []
        self.resource_hits: list[dict] = []
        self.pack_hits: list[dict] = []
        self.resource_page_number = 1
        self.resource_results_kind = None
        self.pack_page_number = 1
        self.resource_total_hits = 0
        self.pack_total_hits = 0
        self.pages: dict[str, tk.Frame] = {}
        self.nav_buttons: dict[str, tk.Button] = {}
        self.profile_labels: list[str] = []
        self.account_info = tk.StringVar(value=self.tr("尚未登录 Microsoft"))
        self.status = tk.StringVar(value=self.tr("就绪 · 欢迎使用 BlockCraft Launcher"))
        self.progress_value = tk.IntVar(value=0)
        self.window_title = "BlockCraft Launcher"
        self.title(f"{self.window_title} {APP_VERSION}  ·  Minecraft Java Edition")
        self.geometry("1180x760")
        self.minsize(990, 630)
        self.configure(bg=BG)
        self.configure_styles()
        self.build_shell()
        self.build_home()
        self.build_versions()
        self.build_resources()
        self.build_packs()
        self.build_settings()
        self.build_logs()
        self.refresh_profiles()
        self.translate_ui()
        self.switch_page("首页")
        self.after(140, self.process_events)
        self.after(400, self.load_versions)
        self.protocol("WM_DELETE_WINDOW", self.close_app)

    def tr(self, source: str) -> str:
        return self.i18n(source)

    def msg_info(self, title, message, **kw):
        return messagebox.showinfo(self.tr(title), self.tr(message), **kw)

    def msg_warning(self, title, message, **kw):
        return messagebox.showwarning(self.tr(title), self.tr(message), **kw)

    def msg_error(self, title, message, **kw):
        return messagebox.showerror(self.tr(title), self.tr(message), **kw)

    def msg_askyesno(self, title, message, **kw):
        return messagebox.askyesno(self.tr(title), self.tr(message), **kw)

    def translate_ui(self):
        """Update existing Tk widgets without destroying pages or user inputs."""
        def visit(widget):
            if isinstance(widget, (tk.Label, tk.Button, tk.Checkbutton)):
                # Only labels/buttons with literal text: textvariable labels
                # must keep their live bindings untouched.
                if not isinstance(widget, tk.Label) or not widget.cget("textvariable"):
                    source = getattr(widget, "_i18n_source", None)
                    if source is None:
                        source = widget.cget("text")
                        widget._i18n_source = source
                    widget.configure(text=self.tr(source))
            if isinstance(widget, ttk.Combobox):
                if widget in (self.version_filter, self.loader_select, self.resource_kind,
                              self.resource_loader, self.resource_category, self.resource_sort,
                              self.pack_loader, self.pack_category, self.pack_sort):
                    source_values = getattr(widget, "_i18n_values", None)
                    if source_values is None:
                        source_values = tuple(widget["values"])
                        widget._i18n_values = source_values
                    index = widget.current()
                    widget["values"] = tuple(self.tr(v) for v in source_values)
                    if index >= 0:
                        widget.current(index)
            if isinstance(widget, ttk.Treeview):
                for column in widget["columns"]:
                    key = "_i18n_heading_" + column
                    source = getattr(widget, key, None)
                    if source is None:
                        source = widget.heading(column, "text")
                        setattr(widget, key, source)
                    widget.heading(column, text=self.tr(source))
            for child in widget.winfo_children():
                visit(child)
        visit(self)
        self.refresh_profiles()
        self.render_hero()

    def change_language(self, event=None):
        selected = self.language_picker.current()
        if selected < 0:
            return
        choice = self.language_codes[selected]
        self.config_model.data["language"] = choice
        self.config_model.save()
        self.i18n.set_language(choice)
        self.translate_ui()
        self.status.set(self.tr("语言已切换") + " · " + LANGUAGES[choice])
        self.log(self.tr("语言已切换") + " · " + LANGUAGES[choice])

    def configure_styles(self):
        style = ttk.Style(self)
        style.theme_use("clam")
        style.configure("TCombobox", font=(UI_FONT, 10), padding=7,
                        fieldbackground=PANEL2, background=PANEL2, foreground=TEXT,
                        arrowcolor=GREEN, bordercolor=BORDER, lightcolor=BORDER, darkcolor=BORDER)
        style.map("TCombobox", fieldbackground=[("readonly", PANEL2)],
                  foreground=[("readonly", TEXT)], selectbackground=[("readonly", PANEL2)])
        style.configure("Dark.Treeview", font=(UI_FONT, 10), rowheight=35,
                        foreground=TEXT, background=PANEL, fieldbackground=PANEL,
                        bordercolor=BORDER, borderwidth=0)
        style.map("Dark.Treeview", background=[("selected", "#31545b")], foreground=[("selected", "#ffffff")])
        style.configure("Dark.Treeview.Heading", font=(UI_FONT, 10, "bold"),
                        foreground=MUTED, background=PANEL2, borderwidth=0, padding=9)
        style.map("Dark.Treeview.Heading", background=[("active", PANEL2)])
        style.configure("Launcher.Horizontal.TProgressbar", troughcolor=PANEL2, background=GREEN,
                        bordercolor=PANEL2, lightcolor=GREEN, darkcolor=GREEN, thickness=7)

    def label(self, parent, text, size=11, color=TEXT, bold=False, **kwargs):
        return tk.Label(parent, text=text, font=(UI_FONT, size, "bold" if bold else "normal"),
                        bg=parent.cget("bg"), fg=color, **kwargs)

    def button(self, parent, text, command, primary=False, width=None):
        kwargs = dict(bg=GREEN if primary else PANEL2, fg="#081813" if primary else TEXT,
                      activebackground="#7ef4c4" if primary else "#35455e",
                      activeforeground="#0c1720" if primary else TEXT,
                      bd=0, relief="flat", highlightthickness=0, padx=18, pady=11, cursor="hand2",
                      font=(UI_FONT, 10, "bold" if primary else "normal"), command=command)
        if width:
            kwargs["width"] = width
        return tk.Button(parent, text=text, **kwargs)

    def entry(self, parent, width=34, show=None):
        return tk.Entry(parent, width=width, show=show, font=(UI_FONT, 11),
                        fg=TEXT, insertbackground=TEXT, bg=PANEL2, bd=0, relief="flat",
                        highlightthickness=1, highlightbackground=BORDER, highlightcolor=GREEN)

    def combo(self, parent, values=(), width=28):
        c = ttk.Combobox(parent, values=values, width=width, state="readonly")
        if values:
            c.current(0)
        return c

    def panel(self, parent, padx=22, pady=19):
        frame = tk.Frame(parent, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
        frame.pack(fill="x", pady=(0, 18), padx=0)
        inner = tk.Frame(frame, bg=PANEL)
        inner.pack(fill="both", expand=True, padx=padx, pady=pady)
        return inner

    def build_shell(self):
        outer = tk.Frame(self, bg=BG)
        outer.pack(fill="both", expand=True)
        side = tk.Frame(outer, bg=SIDEBAR, width=240)
        side.pack(side="left", fill="y")
        side.pack_propagate(False)
        logo = tk.Frame(side, bg=SIDEBAR)
        logo.pack(fill="x", padx=24, pady=(30, 39))
        self.label(logo, "▣  BLOCKCRAFT", size=15, bold=True, color=GREEN).pack(anchor="w")
        self.label(logo, "MINECRAFT JAVA LAUNCHER", size=8, color=MUTED).pack(anchor="w", pady=(7, 0))
        self.label(side, "功能导航", size=9, color=MUTED).pack(anchor="w", padx=27, pady=(0, 10))
        for name, glyph in [("首页", "⌂"), ("游戏版本", "▤"), ("资源中心", "◈"),
                            ("整合包", "▣"), ("设置", "⚙"), ("运行日志", "≡")]:
            btn = tk.Button(side, text=f"{glyph}    {name}", anchor="w", bd=0, highlightthickness=0,
                            bg=SIDEBAR, fg=MUTED, activebackground=PANEL2, activeforeground=TEXT,
                            padx=27, pady=14, cursor="hand2", font=(UI_FONT, 10),
                            command=lambda x=name: self.switch_page(x))
            btn.pack(fill="x", pady=2)
            self.nav_buttons[name] = btn
        bottom = tk.Frame(side, bg=SIDEBAR)
        bottom.pack(side="bottom", fill="x", padx=22, pady=25)
        self.label(bottom, "账户", size=9, color=MUTED).pack(anchor="w")
        tk.Label(bottom, textvariable=self.account_info, bg=SIDEBAR, fg=TEXT,
                 font=(UI_FONT, 10), wraplength=176, justify="left").pack(anchor="w", pady=8)
        self.button(bottom, "微软正版登录", self._begin_browser_login, primary=True).pack(fill="x", pady=(0, 8))
        offline_button = self.button(bottom, "离线模式（无需账号）", self._begin_offline_login)
        offline_button.configure(font=(UI_FONT, 9), padx=5, wraplength=184)
        offline_button.pack(fill="x")
        self.content = tk.Frame(outer, bg=BG)
        self.content.pack(side="left", fill="both", expand=True)
        self.page_root = tk.Frame(self.content, bg=BG)
        footer = tk.Frame(self.content, bg=BG)
        footer.pack(side="bottom", fill="x", padx=32, pady=(7, 13))
        tk.Label(footer, textvariable=self.status, bg=BG, fg=MUTED,
                 font=(UI_FONT, 9), anchor="w").pack(side="left", fill="x", expand=True)
        self.progress = ttk.Progressbar(footer, style="Launcher.Horizontal.TProgressbar",
                                        length=170, mode="determinate", maximum=100,
                                        variable=self.progress_value)
        self.progress.pack(side="right", padx=(12, 0))
        # Reserve the status bar first; otherwise tall search pages hide it.
        self.page_root.pack(fill="both", expand=True, padx=32, pady=(26, 8))

    def new_page(self, name):
        page = tk.Frame(self.page_root, bg=BG)
        self.pages[name] = page
        return page

    def page_title(self, parent, title, subtitle):
        self.label(parent, title, size=23, bold=True).pack(anchor="w")
        self.label(parent, subtitle, size=10, color=MUTED).pack(anchor="w", pady=(4, 20))

    def switch_page(self, name):
        for page in self.pages.values():
            page.pack_forget()
        self.pages[name].pack(fill="both", expand=True)
        for label, button in self.nav_buttons.items():
            button.configure(bg="#233348" if label == name else SIDEBAR,
                             fg=GREEN if label == name else MUTED)
        if name == "首页":
            self.render_hero()

    def build_home(self):
        page = self.new_page("首页")
        self.page_title(page, "欢迎回到方块世界", "选择一个实例，构建你的下一段冒险。")
        self.hero = tk.Canvas(page, height=198, bg="#233047", highlightthickness=0)
        self.hero.pack(fill="x", pady=(0, 20))
        self.hero.bind("<Configure>", lambda event: self.render_hero())
        self._hero_image = None
        inner = self.panel(page)
        self.label(inner, "我的游戏实例", size=14, bold=True).pack(anchor="w", pady=(0, 12))
        line = tk.Frame(inner, bg=PANEL)
        line.pack(fill="x")
        self.home_profile = self.combo(line, width=44)
        self.home_profile.pack(side="left", fill="x", expand=True, padx=(0, 12))
        self.button(line, "打开游戏文件夹", self.open_selected_profile_folder).pack(side="right")
        self.label(inner, "每个实例的存档、模组、光影和资源包独立保存。", color=MUTED, size=9).pack(anchor="w", pady=(11, 18))
        actions = tk.Frame(inner, bg=PANEL)
        actions.pack(fill="x")
        self.button(actions, "▶  启动游戏", self.launch_game, primary=True).pack(side="left")
        self.button(actions, "+  安装新版本", lambda: self.switch_page("游戏版本")).pack(side="left", padx=12)
        self.button(actions, "移除实例记录", self.remove_profile).pack(side="left")
        inner2 = self.panel(page)
        self.label(inner2, "快速开始", size=13, bold=True).pack(anchor="w", pady=(0, 10))
        self.label(inner2, "① 在「游戏版本」中安装原版或加载器。\n② 在「资源中心」安装模组、光影与资源包。\n③ 选择离线模式或使用 Microsoft 正版账号启动。",
                   size=10, color=MUTED, justify="left").pack(anchor="w")

    def render_hero(self):
        if not hasattr(self, "hero") or self.hero.winfo_width() < 2:
            return
        w, h = self.hero.winfo_width(), 198
        self.hero.delete("all")
        bg_path = self.config_model.data.get("background", "")
        if bg_path and Image is not None and Path(bg_path).is_file():
            try:
                with Image.open(bg_path) as source:
                    img = ImageOps.fit(source.convert("RGB"), (w, h))
                img = ImageEnhance.Brightness(img).enhance(.43)
                self._hero_image = ImageTk.PhotoImage(img)
                self.hero.create_image(0, 0, anchor="nw", image=self._hero_image)
            except (OSError, ValueError):
                self.hero.configure(bg="#233047")
        else:
            self.hero.configure(bg="#233047")
            self.hero.create_rectangle(0, 0, w, h, fill="#203852", outline="")
            self.hero.create_polygon(w*.65, 0, w, 0, w, h, w*.47, h, fill="#203b48", outline="")
            self.hero.create_polygon(w*.82, 0, w, h*.18, w, h, w*.61, h, fill="#26524c", outline="")
        self.hero.create_text(29, 34, anchor="nw", text="JAVA EDITION  •  YOUR WORLD, YOUR RULES",
                              fill=GREEN, font=(UI_FONT, 9, "bold"))
        self.hero.create_text(28, 69, anchor="nw", text=self.tr("创造 · 探索 · 无限可能"), fill="white",
                              font=(UI_FONT, 23, "bold"))
        self.hero.create_text(30, 125, anchor="nw", text=self.tr("全版本 · 模组管理 · 自定义外观"), fill="#c9d4e0",
                              font=(UI_FONT, 11))

    def build_versions(self):
        page = self.new_page("游戏版本")
        self.page_title(page, "安装 Minecraft", "来自 Mojang 官方版本列表 · 可选 Fabric / Forge / NeoForge / Quilt")
        filters = tk.Frame(page, bg=BG)
        filters.pack(fill="x", pady=(0, 12))
        self.version_filter = self.combo(filters, ["全部版本", "正式版", "快照版", "远古 Beta", "远古 Alpha"], width=15)
        self.version_filter.pack(side="left", padx=(0, 10))
        self.version_filter.bind("<<ComboboxSelected>>", lambda e: self.filter_versions())
        self.version_search = self.entry(filters, width=25)
        self.version_search.pack(side="left", padx=(0, 10), ipady=7)
        self.version_search.insert(0, "")
        self.version_search.bind("<KeyRelease>", lambda e: self.filter_versions())
        self.button(filters, "刷新列表", self.load_versions).pack(side="left")
        frame = tk.Frame(page, bg=PANEL, highlightthickness=1, highlightbackground=BORDER)
        frame.pack(fill="both", expand=True, pady=(0, 15))
        self.version_tree = self.tree(frame, (("version", "版本号", 280), ("type", "类型", 160),
                                              ("time", "发布日期", 200)))
        row = tk.Frame(page, bg=BG)
        row.pack(fill="x")
        self.label(row, "加载器", size=10, color=MUTED).pack(side="left", padx=(0, 10))
        self.loader_select = self.combo(row, list(LOADERS), width=15)
        self.loader_select.pack(side="left", padx=(0, 14))
        self.button(row, "安装选中版本", self.install_game, primary=True).pack(side="left")
        self.label(row, "可双击版本进行安装", size=9, color=MUTED).pack(side="right")
        self.version_tree.bind("<Double-1>", lambda e: self.install_game())

    def tree(self, parent, columns):
        wrapper = tk.Frame(parent, bg=PANEL)
        wrapper.pack(fill="both", expand=True, padx=1, pady=1)
        names = [x[0] for x in columns]
        view = ttk.Treeview(wrapper, columns=names, show="headings", style="Dark.Treeview", selectmode="browse")
        for key, title, width in columns:
            view.heading(key, text=title)
            view.column(key, width=width, anchor="w", stretch=True)
        scroll = ttk.Scrollbar(wrapper, orient="vertical", command=view.yview)
        view.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        view.pack(side="left", fill="both", expand=True)
        return view

    def build_resources(self):
        page = self.new_page("资源中心")
        self.page_title(page, "Modrinth 资源中心", "搜索模组、光影与资源包 · 可先搜索后选择安装实例")
        first = tk.Frame(page, bg=BG)
        first.pack(fill="x", pady=(0, 10))
        self.label(first, "目标实例", color=MUTED, size=10).pack(side="left")
        self.resource_profile = self.combo(first, width=38)
        self.resource_profile.pack(side="left", padx=10, fill="x", expand=True)
        self.resource_profile.bind("<<ComboboxSelected>>", self.use_profile_for_search)
        self.resource_kind = self.combo(first, ["模组", "光影", "资源包"], width=12)
        self.resource_kind.pack(side="right")
        self.resource_kind.bind("<<ComboboxSelected>>", lambda e: self.reset_resource_category())
        searchbar = tk.Frame(page, bg=BG)
        searchbar.pack(fill="x", pady=(0, 10))
        self.resource_query = self.entry(searchbar, width=48)
        self.resource_query.pack(side="left", fill="x", expand=True, ipady=8, padx=(0, 10))
        self.resource_query.bind("<Return>", lambda e: self.search_resource())
        self.button(searchbar, "搜索资源", self.search_resource, primary=True).pack(side="left")
        filters = tk.Frame(page, bg=BG)
        filters.pack(fill="x", pady=(0, 11))
        self.label(filters, "游戏版本", size=9, color=MUTED).pack(side="left")
        self.resource_version = self.entry(filters, width=10)
        self.resource_version.pack(side="left", padx=(6, 12), ipady=7)
        self.label(filters, "加载器", size=9, color=MUTED).pack(side="left")
        self.resource_loader = self.combo(filters, ["全部加载器", "Fabric", "Forge", "NeoForge", "Quilt"], width=12)
        self.resource_loader.pack(side="left", padx=(6, 12))
        self.label(filters, "分类", size=9, color=MUTED).pack(side="left")
        self.resource_category = self.combo(filters, ["全部分类"], width=14)
        self.resource_category.pack(side="left", padx=(6, 12))
        self.label(filters, "排序", size=9, color=MUTED).pack(side="left")
        self.resource_sort = self.combo(filters, ["相关度", "下载量", "关注数", "最新发布", "最近更新"], width=12)
        self.resource_sort.pack(side="left", padx=(6, 0))
        self.reset_resource_category()
        self.label(page, "提示：搜索不要求安装实例；安装时按所选实例实际版本再次检查兼容性。", color=MUTED, size=9).pack(side="bottom", anchor="w", pady=(5, 0))
        pager = tk.Frame(page, bg=BG)
        pager.pack(side="bottom", fill="x", pady=(0, 9))
        self.resource_page_label = tk.StringVar(value="输入关键词后搜索，也可留空浏览热门项目")
        self.label(pager, "", color=MUTED, size=9, textvariable=self.resource_page_label).pack(side="left", fill="x", expand=True)
        self.resource_next = self.button(pager, "下一页", lambda: self.search_resource(self.resource_page_number + 1))
        self.resource_next.pack(side="right", padx=(7, 0))
        self.resource_prev = self.button(pager, "上一页", lambda: self.search_resource(self.resource_page_number - 1))
        self.resource_prev.pack(side="right")
        self.resource_prev.configure(state="disabled")
        self.resource_next.configure(state="disabled")
        buttons = tk.Frame(page, bg=BG)
        buttons.pack(side="bottom", fill="x")
        self.button(buttons, "下载并安装选中资源", self.install_resource, primary=True).pack(side="left")
        self.button(buttons, "查看详情", self.show_resource_details).pack(side="left", padx=(10, 0))
        self.button(buttons, "导入本地文件", self.import_resource).pack(side="left", padx=10)
        self.button(buttons, "查看项目主页", self.open_resource_page).pack(side="left")
        frame = tk.Frame(page, bg=PANEL, highlightbackground=BORDER, highlightthickness=1)
        frame.pack(fill="both", expand=True, pady=(0, 8))
        self.resource_tree = self.tree(frame, (("title", "资源名称", 320), ("author", "作者", 130),
                                               ("downloads", "下载次数", 115), ("versions", "支持版本", 155)))
        self.resource_tree.bind("<Double-1>", lambda e: self.show_resource_details())

    def build_packs(self):
        page = self.new_page("整合包")
        self.page_title(page, "整合包管理", "Modrinth .mrpack 在线安装、本地导入与兼容版本筛选")
        line = tk.Frame(page, bg=BG)
        line.pack(fill="x", pady=(0, 10))
        self.pack_query = self.entry(line, width=44)
        self.pack_query.pack(side="left", fill="x", expand=True, ipady=8, padx=(0, 10))
        self.pack_query.bind("<Return>", lambda e: self.search_packs())
        self.button(line, "搜索整合包", self.search_packs, primary=True).pack(side="left")
        filters = tk.Frame(page, bg=BG)
        filters.pack(fill="x", pady=(0, 11))
        self.label(filters, "游戏版本", color=MUTED, size=9).pack(side="left")
        self.pack_version = self.entry(filters, width=10)
        self.pack_version.pack(side="left", padx=(6, 12), ipady=7)
        self.label(filters, "加载器", color=MUTED, size=9).pack(side="left")
        self.pack_loader = self.combo(filters, ["全部加载器", "Fabric", "Forge", "NeoForge", "Quilt"], width=12)
        self.pack_loader.pack(side="left", padx=(6, 12))
        self.label(filters, "分类", color=MUTED, size=9).pack(side="left")
        self.pack_category = self.combo(filters, ["全部分类", "冒险", "战斗", "魔法", "优化", "科技", "轻量", "任务", "多人", "高难度"], width=12)
        self.pack_category.pack(side="left", padx=(6, 12))
        self.label(filters, "排序", color=MUTED, size=9).pack(side="left")
        self.pack_sort = self.combo(filters, ["相关度", "下载量", "关注数", "最新发布", "最近更新"], width=12)
        self.pack_sort.pack(side="left", padx=(6, 0))
        self.label(page, "说明：部分整合包依赖外部下载或作者授权，无法保证全部自动安装。", color=MUTED, size=9).pack(side="bottom", anchor="w", pady=(11, 0))
        pager = tk.Frame(page, bg=BG)
        pager.pack(side="bottom", fill="x", pady=(0, 9))
        self.pack_page_label = tk.StringVar(value="输入关键词后搜索，也可留空浏览热门整合包")
        self.label(pager, "", size=9, color=MUTED, textvariable=self.pack_page_label).pack(side="left", fill="x", expand=True)
        self.pack_next = self.button(pager, "下一页", lambda: self.search_packs(self.pack_page_number + 1))
        self.pack_next.pack(side="right", padx=(7, 0))
        self.pack_prev = self.button(pager, "上一页", lambda: self.search_packs(self.pack_page_number - 1))
        self.pack_prev.pack(side="right")
        self.pack_prev.configure(state="disabled")
        self.pack_next.configure(state="disabled")
        buttons = tk.Frame(page, bg=BG)
        buttons.pack(side="bottom", fill="x")
        self.button(buttons, "一键安装选中整合包", self.install_pack, primary=True).pack(side="left")
        self.button(buttons, "查看详情", self.show_pack_details).pack(side="left", padx=10)
        self.button(buttons, "从本地 .mrpack 导入", self.import_pack).pack(side="left", padx=10)
        self.button(buttons, "打开整合包网页", self.open_pack_page).pack(side="left")
        frame = tk.Frame(page, bg=PANEL, highlightthickness=1, highlightbackground=BORDER)
        frame.pack(fill="both", expand=True, pady=(0, 8))
        self.pack_tree = self.tree(frame, (("title", "整合包", 325), ("author", "作者", 135),
                                           ("downloads", "下载量", 115), ("versions", "支持版本", 155)))
        self.pack_tree.bind("<Double-1>", lambda e: self.show_pack_details())

    def build_settings(self):
        page = self.new_page("设置")
        self.page_title(page, "启动器设置", "网络加速、Java 运行时、账户认证和自定义背景")
        scrollbox = tk.Frame(page, bg=BG)
        scrollbox.pack(fill="both", expand=True)
        scroller = tk.Scrollbar(scrollbox, orient="vertical")
        scroller.pack(side="right", fill="y")
        canvas = tk.Canvas(scrollbox, bg=BG, highlightthickness=0, yscrollcommand=scroller.set)
        canvas.pack(side="left", fill="both", expand=True)
        scroller.configure(command=canvas.yview)
        body = tk.Frame(canvas, bg=BG)
        body_window = canvas.create_window(0, 0, anchor="nw", window=body)
        body.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda e: canvas.itemconfigure(body_window, width=e.width))
        def wheel(event):
            canvas.yview_scroll(-int(event.delta / 120), "units")
        canvas.bind("<Enter>", lambda e: canvas.bind_all("<MouseWheel>", wheel))
        canvas.bind("<Leave>", lambda e: canvas.unbind_all("<MouseWheel>"))
        self.label(body, "语言 / Language", size=11, color=GREEN, bold=True).pack(anchor="w", pady=(4, 6))
        self.language_codes = list(LANGUAGES)
        self.language_picker = self.combo(body, [display_language(c) for c in self.language_codes], width=51)
        try:
            self.language_picker.current(self.language_codes.index(self.config_model.data.get("language", "auto")))
        except ValueError:
            self.language_picker.current(0)
        self.language_picker.pack(anchor="w", fill="x", pady=(0, 5))
        self.language_picker.bind("<<ComboboxSelected>>", self.change_language)
        self.label(body, "翻译不完整时显示英文", size=9, color=MUTED).pack(anchor="w", pady=(0, 18))
        fields = [
            ("Minecraft 文件目录", "root", True, "dir"),
            ("Java 可执行文件（留空自动选择）", "java", True, "file"),
            ("HTTP / HTTPS 代理（可留空）", "proxy", False, None),
            ("自定义背景图片（PNG / JPG）", "background", True, "image"),
        ]
        self.setting_inputs = {}
        for title, key, browse, mode in fields:
            self.label(body, title, size=10, color=MUTED).pack(anchor="w", pady=(4, 5))
            group = tk.Frame(body, bg=BG)
            group.pack(fill="x", pady=(0, 10))
            widget = self.entry(group, width=64)
            widget.insert(0, str(self.config_model.data.get(key, "")))
            widget.pack(side="left", fill="x", expand=True, ipady=7, padx=(0, 9 if browse else 0))
            self.setting_inputs[key] = widget
            if browse:
                self.button(group, "浏览", lambda k=key, m=mode: self.browse_setting(k, m)).pack(side="right")
        line = tk.Frame(body, bg=BG)
        line.pack(fill="x", pady=10)
        self.use_mirror = tk.BooleanVar(value=bool(self.config_model.data["mirror"]))
        tk.Checkbutton(line, text="优先使用 BMCLAPI 国内镜像，失败回退官方", variable=self.use_mirror,
                       bg=BG, fg=TEXT, activebackground=BG, activeforeground=TEXT,
                       selectcolor=PANEL2, font=(UI_FONT, 10)).pack(side="left")
        # minecraft-launcher-lib 8 downloads libraries/assets concurrently;
        # offer a bounded user control instead of unbounded network threads.
        netrow = tk.Frame(body, bg=BG)
        netrow.pack(fill="x", pady=(3, 14))
        self.label(netrow, "同时下载文件数", size=10, color=MUTED).pack(side="left", padx=(0, 15))
        options = ("4", "8", "12", "16", "24", "32")
        self.download_threads = self.combo(netrow, options, width=8)
        current_threads = str(self.config_model.data.get("download_threads", 12))
        self.download_threads.set(current_threads if current_threads in options else "12")
        self.download_threads.pack(side="left")
        self.label(body, "建议先用 12 或 16；网络不稳定、频繁超时可改为 4 或 8。", size=9,
                   color=MUTED).pack(anchor="w", pady=(0, 6))
        memline = tk.Frame(body, bg=BG)
        memline.pack(fill="x", pady=(4, 4))
        self.memory_var = tk.IntVar(value=int(self.config_model.data["memory_mb"]))
        tk.Label(memline, text="游戏最大内存（MB）", bg=BG, fg=MUTED,
                 font=(UI_FONT, 10)).pack(side="left")
        self.memory_label = tk.Label(memline, text=f"{self.memory_var.get()} MB", bg=BG, fg=GREEN)
        self.memory_label.pack(side="right")
        scale = tk.Scale(body, from_=1024, to=16384, resolution=512, orient="horizontal", bg=BG,
                         fg=TEXT, troughcolor=PANEL2, activebackground=GREEN, highlightthickness=0,
                         showvalue=False, variable=self.memory_var,
                         command=lambda v: self.memory_label.configure(text=f"{int(float(v))} MB"))
        scale.pack(fill="x")
        self.label(body, "Python 运行环境（仅 Windows EXE 引导版）", size=11,
                   color=GREEN, bold=True).pack(anchor="w", pady=(15, 6))
        self.label(body, "每 7 天自动检查兼容的 Python 维护更新；升级前会询问你。",
                   size=9, color=MUTED).pack(anchor="w", pady=(0, 6))
        self.button(body, "下次启动立即检查 Python 更新", self.request_python_update).pack(anchor="w", pady=(0, 10))
        self.button(body, "保存设置", self.save_settings, primary=True).pack(anchor="w", pady=10)
        self.label(body, "微软正版登录将在官方浏览器中完成，普通玩家无需填写 Azure ID。",
                   color=MUTED, size=9).pack(anchor="w")

    def build_logs(self):
        page = self.new_page("运行日志")
        self.page_title(page, "运行日志", "安装过程、错误与下载状态（不记录账号令牌）")
        wrap = tk.Frame(page, bg=PANEL)
        wrap.pack(fill="both", expand=True)
        self.logbox = tk.Text(wrap, bg=PANEL, fg="#ccd7e8", insertbackground=TEXT, bd=0,
                              padx=18, pady=18, font=("Consolas", 10), wrap="word", state="disabled")
        self.logbox.pack(fill="both", expand=True)
        self.button(page, "清空显示", self.clear_log).pack(anchor="e", pady=(12, 0))

    def log(self, message: str):
        text = f"[{datetime.now():%H:%M:%S}] {self.tr(message)}\n"
        if hasattr(self, "logbox"):
            self.logbox.configure(state="normal")
            self.logbox.insert("end", text)
            self.logbox.see("end")
            self.logbox.configure(state="disabled")

    def clear_log(self):
        self.logbox.configure(state="normal")
        self.logbox.delete("1.0", "end")
        self.logbox.configure(state="disabled")

    def work(self, label: str, task, done=None):
        if self.busy:
            self.msg_info("任务进行中", "请等待当前任务完成后再操作。")
            return
        self.busy = True
        self.status.set(self.tr(label))
        self.progress_value.set(0)
        self.log(self.tr(label))

        def run():
            try:
                result = task(lambda msg, cur=None, total=None: self.events.put(("progress", msg, cur, total)))
                self.events.put(("success", label, result, done))
            except Exception as exc:
                self.events.put(("error", label, str(exc), traceback.format_exc()))

        threading.Thread(target=run, daemon=True, name="blockcraft-worker").start()

    def process_events(self):
        try:
            while True:
                event = self.events.get_nowait()
                kind = event[0]
                if kind == "progress":
                    _, msg, cur, total = event
                    self.status.set(self.tr(msg))
                    if cur is not None and total:
                        self.progress_value.set(min(100, max(0, int(cur * 100 / total))))
                    # Avoid extremely noisy logs when installing thousands of assets.
                    if (cur is None or cur == 0 or (total and cur == total)):
                        self.log(self.tr(msg))
                elif kind == "success":
                    _, label, result, callback = event
                    self.busy = False
                    self.status.set("✓  " + self.tr(label) + " " + self.tr("完成"))
                    self.progress_value.set(100)
                    self.log(self.tr("完成：") + self.tr(label))
                    if callback:
                        try:
                            callback(result)
                        except Exception as err:
                            self.log("界面更新失败：" + str(err))
                    self.refresh_profiles()
                elif kind == "error":
                    _, label, error, stack = event
                    self.busy = False
                    if label == "Microsoft 正版登录":
                        self._close_login_window()
                        if self._login_cancelled:
                            self._login_cancelled = False
                            self.status.set(self.tr("网页登录已取消"))
                            continue
                    self.status.set("✕  " + self.tr(label) + " " + self.tr("失败"))
                    self.progress_value.set(0)
                    self.log(self.tr(label) + " " + self.tr("失败：") + error)
                    # Only show generic diagnostics; auth libraries may put URLs containing
                    # sensitive codes in stack traces, so don't display raw tracebacks.
                    self.msg_error("操作失败", f"{label}失败：\n{error}\n\n请检查网络、版本兼容性和设置。")
        except queue.Empty:
            pass
        if self.winfo_exists():
            self.after(140, self.process_events)

    def load_versions(self):
        self.work("获取游戏版本列表", lambda report: self.mc.versions(), self.got_versions)

    def got_versions(self, versions):
        self.all_versions = versions
        self.filter_versions()
        self.log(self.tr("版本列表已加载，共 ") + str(len(versions)) + self.tr(" 个版本。"))

    def filter_versions(self):
        names = {"全部版本": None, "正式版": "release", "快照版": "snapshot",
                 "远古 Beta": "old_beta", "远古 Alpha": "old_alpha"}
        chosen = [None, "release", "snapshot", "old_beta", "old_alpha"][max(0, self.version_filter.current())]
        query = self.version_search.get().lower().strip()
        self.version_tree.delete(*self.version_tree.get_children())
        for v in self.all_versions:
            if chosen and v.get("type") != chosen:
                continue
            if query and query not in v.get("id", "").lower():
                continue
            self.version_tree.insert("", "end", iid=v["id"], values=(v["id"], v.get("type", ""),
                                                                        v.get("releaseTime", "")[:10]))

    def install_game(self):
        chosen = self.version_tree.selection()
        if not chosen:
            self.msg_info("选择版本", "请先选中一个 Minecraft 版本。")
            return
        version = chosen[0]
        loader = list(LOADERS.values())[max(0, self.loader_select.current())]
        if not self.msg_askyesno("安装新实例", f"准备安装：\nMinecraft {version}\n加载器：{self.loader_select.get()}\n\n要继续吗？"):
            return
        self.work(f"安装 Minecraft {version} / {loader}",
                  lambda report: self.mc.install(version, loader, f"{version} [{loader}]", report),
                  lambda p: (self.log("新实例：" + p["name"]), self.switch_page("首页")))

    def refresh_profiles(self):
        profiles = self.config_model.data["profiles"]
        self.profile_labels = [f"{p['name']}  •  {p['base_version']}  ({p['loader']})" for p in profiles]
        for box in (self.home_profile, self.resource_profile):
            previous = box.current()
            box["values"] = self.profile_labels
            if profiles:
                box.current(previous if 0 <= previous < len(profiles) else 0)
            else:
                box.set("")
        if self.config_model.data.get("account_mode") == "offline":
            name = self.config_model.data.get("offline_name", "")
            self.account_info.set(self.tr("离线模式：") + name if name else self.tr("未设置离线玩家名称"))
        else:
            name = self.config_model.data.get("account_name", "")
            self.account_info.set(name or self.tr("尚未登录 Microsoft"))

    def selected_profile(self, box):
        index = box.current()
        profiles = self.config_model.data["profiles"]
        if index < 0 or index >= len(profiles):
            raise ValueError("请先在「游戏版本」中安装或导入一个实例")
        return profiles[index]

    def open_selected_profile_folder(self):
        try:
            open_folder(self.selected_profile(self.home_profile)["dir"])
        except Exception as exc:
            self.msg_error("无法打开", str(exc))

    def remove_profile(self):
        try:
            selected = self.selected_profile(self.home_profile)
        except ValueError as exc:
            self.msg_info("没有实例", str(exc))
            return
        if self.msg_askyesno("确认移除", f"只从启动器列表移除「{selected['name']}」吗？\n不会删除存档或目录。"):
            self.config_model.remove_profile(selected["id"])
            self.refresh_profiles()

    def launch_game(self):
        try:
            profile = self.selected_profile(self.home_profile)
            if self.config_model.data.get("account_mode") == "offline":
                # Validate before spawning a worker; never call Microsoft's APIs in offline mode.
                identity = offline_account(self.config_model.data.get("offline_name", ""))
            else:
                identity = None
                if not self.config_model.data.get("account_name"):
                    raise ValueError("请先选择离线模式或登录 Microsoft 正版账号")
        except ValueError as exc:
            self.msg_info("无法启动", str(exc))
            return

        def task(report):
            account = identity if identity is not None else (self.auth.current or self.auth.refresh())
            return self.mc.launch(profile, account, report)

        self.work("启动 Minecraft", task,
                  lambda pid: self.msg_info("游戏已启动", f"已提交启动进程（PID: {pid}）。\n游戏日志存于启动器数据目录。"))

    def _close_login_window(self):
        top = self._login_window
        self._login_window = None
        if top is not None and top.winfo_exists():
            top.grab_release()
            top.destroy()

    def _login_succeeded(self, info):
        self._close_login_window()
        self.config_model.data["account_mode"] = "microsoft"
        self.config_model.save()
        self.refresh_profiles()
        self.msg_info("登录成功", "当前账号：" + info["name"])

    def login_dialog(self):
        """Compatibility entry: take players directly to official web login."""
        return self._begin_browser_login()

    def _begin_offline_login(self):
        """Select a locally generated game profile. Never requests passwords/tokens."""
        top = tk.Toplevel(self)
        top.title(self.tr("离线模式"))
        top.geometry("520x280")
        top.resizable(False, False)
        top.configure(bg=PANEL)
        top.transient(self)
        top.grab_set()
        self.label(top, "设置离线玩家名称", size=17, bold=True).pack(anchor="w", padx=23, pady=(22, 9))
        self.label(top, "3～16 位英文字母、数字或下划线；同一名称会保留相同的本地玩家 UUID。",
                   color=MUTED, wraplength=470, justify="left").pack(anchor="w", padx=23)
        username = self.entry(top, width=38)
        username.insert(0, self.config_model.data.get("offline_name", "") or "Player123")
        username.pack(fill="x", padx=23, pady=(12, 14), ipady=5)
        self.label(top, "离线身份无法进入要求正版验证的服务器或 Realms。", color=MUTED,
                   wraplength=470, justify="left").pack(anchor="w", padx=23)
        actions = tk.Frame(top, bg=PANEL)
        actions.pack(fill="x", padx=23, pady=(19, 12))

        def enable_offline():
            try:
                identity = offline_account(username.get().strip())
            except ValueError as exc:
                self.msg_warning("名称无效", str(exc), parent=top)
                return
            self.config_model.data["offline_name"] = identity["name"]
            self.config_model.data["account_mode"] = "offline"
            self.config_model.save()
            top.grab_release()
            top.destroy()
            self.refresh_profiles()
            self.msg_info("离线模式已启用", "当前离线玩家：" + identity["name"])

        self.button(actions, "启用离线模式", enable_offline, primary=True).pack(side="left")
        self.button(actions, "取消", top.destroy).pack(side="right")
        username.bind("<Return>", lambda event: enable_offline())
        username.focus_set()
        self.translate_ui()

    def _begin_device_login(self):
        self._login_cancelled = False
        self.work("获取设备登录代码", lambda report: self.auth.start_device(), self._device_started)

    def _device_started(self, login_data: dict):
        """Present the one-time code; polling happens only in the worker."""
        top = tk.Toplevel(self)
        self._login_window = top
        top.title(self.tr("设备代码登录"))
        top.geometry("650x320")
        top.resizable(False, False)
        top.configure(bg=PANEL)
        top.transient(self)
        top.grab_set()
        self.label(top, "Microsoft 设备代码登录", size=17, bold=True).pack(
            anchor="w", padx=23, pady=(24, 7))
        self.label(top, "在微软官方网页输入以下一次性代码（不要发送给其他人）：",
                   color=MUTED).pack(anchor="w", padx=23)
        self.label(top, login_data["user_code"], size=27, bold=True, color=GREEN).pack(
            anchor="w", padx=23, pady=(9, 8))
        self.label(top, "代码有效期约 " + str(max(1, login_data["expires_in"] // 60)) + " 分钟，授权后启动器会自动登录。",
                   color=MUTED).pack(anchor="w", padx=23)
        row = tk.Frame(top, bg=PANEL)
        row.pack(fill="x", padx=23, pady=(22, 6))
        uri = login_data["verification_uri"]
        code = login_data["user_code"]

        def copy_code():
            self.clipboard_clear()
            self.clipboard_append(code)
            self.update_idletasks()

        self.button(row, "复制代码", copy_code).pack(side="left", padx=(0, 10))
        self.button(row, "打开微软验证网页", lambda: webbrowser.open(uri), primary=True).pack(side="left")

        def cancel():
            self._login_cancelled = True
            self.auth.cancel_device()
            self._close_login_window()

        self.button(row, "取消登录", cancel).pack(side="right")
        top.protocol("WM_DELETE_WINDOW", cancel)
        self.translate_ui()
        self.work("Microsoft 正版登录", lambda report: self.auth.await_device_login(),
                  self._login_succeeded)
        if not webbrowser.open(uri):
            self.msg_warning("浏览器未打开", "请点击“打开微软验证网页”后输入上面的代码。", parent=top)

    def _begin_browser_login(self):
        if self.busy:
            self.msg_info("任务进行中", "请等待当前任务完成后再操作。")
            return
        try:
            url = self.auth.start_browser()
        except Exception as exc:
            self.msg_info("Microsoft 登录不可用", str(exc))
            return
        top = tk.Toplevel(self)
        self._login_window = top
        self._login_cancelled = False
        top.title(self.tr("微软正版网页登录"))
        top.geometry("690x260")
        top.resizable(False, False)
        top.configure(bg=PANEL)
        top.transient(self)
        top.grab_set()
        self.label(top, "在浏览器完成 Microsoft 登录", size=17, bold=True).pack(
            anchor="w", padx=23, pady=(25, 12))
        self.label(top, "在微软官方网页输入账号密码或使用通行密钥；启动器不会读取密码。授权后自动返回。",
                   size=10, color=MUTED, wraplength=630, justify="left").pack(anchor="w", padx=23)
        self.label(top, "等待网页授权（最多 4 分钟）。如浏览器没有自动打开，请点击下方按钮。",
                   size=9, color=MUTED, wraplength=630, justify="left").pack(anchor="w", padx=23, pady=(10, 15))
        row = tk.Frame(top, bg=PANEL)
        row.pack(fill="x", padx=23, pady=(3, 8))
        self.button(row, "重新打开浏览器", lambda: webbrowser.open(url)).pack(side="left")

        def cancel():
            self._login_cancelled = True
            self.auth.cancel_browser()
            self._close_login_window()

        self.button(row, "取消登录", cancel).pack(side="right")
        top.protocol("WM_DELETE_WINDOW", cancel)
        self.translate_ui()
        self.work("Microsoft 正版登录", lambda report: self.auth.await_browser_login(),
                  self._login_succeeded)
        if not webbrowser.open(url):
            self.msg_warning("浏览器未打开", "请点击“重新打开浏览器”，在默认浏览器中登录。", parent=top)

    def _resource_kind(self):
        return ("mod", "shader", "resourcepack")[max(0, self.resource_kind.current())]

    @staticmethod
    def _search_sort(box):
        return ("relevance", "downloads", "follows", "newest", "updated")[max(0, box.current())]

    @staticmethod
    def _search_loader(box):
        return ("", "fabric", "forge", "neoforge", "quilt")[max(0, box.current())]

    _CATEGORY_NAMES = {
        "mod": {"全部分类": "", "冒险": "adventure", "装饰": "decoration", "装备": "equipment",
                "食物": "food", "优化": "optimization", "科技": "technology", "魔法": "magic",
                "世界生成": "worldgen", "工具": "utility", "生物": "mobs", "存储": "storage"},
        "shader": {"全部分类": "", "写实": "realistic", "原版风格": "vanilla-like",
                   "幻想": "fantasy", "轻量": "lightweight", "半写实": "semi-realistic"},
        "resourcepack": {"全部分类": "", "写实": "realistic", "原版风格": "vanilla-like",
                         "卡通": "cartoon", "简洁": "simplistic", "主题": "themed", "音效": "audio"},
        "modpack": {"全部分类": "", "冒险": "adventure", "战斗": "combat", "魔法": "magic",
                    "优化": "optimization", "科技": "technology", "轻量": "lightweight",
                    "任务": "quests", "多人": "multiplayer", "高难度": "challenging"}
    }

    def reset_resource_category(self):
        kind = self._resource_kind()
        self.resource_category["values"] = list(self.tr(k) for k in self._CATEGORY_NAMES[kind])
        self.resource_category.current(0)
        # Prevent installing a stale search result under a different resource type.
        self.resource_hits = []
        self.resource_results_kind = None
        self.resource_page_number = 1
        self.resource_total_hits = 0
        if hasattr(self, "resource_tree"):
            self.resource_tree.delete(*self.resource_tree.get_children())
            self.resource_page_label.set(self.tr("请搜索以查看结果"))
            self.resource_prev.configure(state="disabled")
            self.resource_next.configure(state="disabled")
        # Avoid interpreting already-translated dynamic strings as new source strings.
        self.resource_category._i18n_values = tuple(self._CATEGORY_NAMES[kind])

    def use_profile_for_search(self, event=None):
        try:
            profile = self.selected_profile(self.resource_profile)
        except ValueError:
            return
        self.resource_version.delete(0, "end")
        self.resource_version.insert(0, profile["base_version"])
        try:
            self.resource_loader.current(("", "fabric", "forge", "neoforge", "quilt").index(profile["loader"]))
        except ValueError:
            self.resource_loader.current(0)

    @staticmethod
    def _list_version(hit):
        versions = hit.get("versions") or []
        return ", ".join(str(v) for v in versions[-3:][::-1]) if isinstance(versions, list) else ""

    def search_resource(self, page=1):
        kind = self._resource_kind()
        category_names = list(self._CATEGORY_NAMES[kind].values())
        category = category_names[max(0, self.resource_category.current())]
        version = self.resource_version.get().strip()
        loader = self._search_loader(self.resource_loader) if kind == "mod" else ""
        sort = self._search_sort(self.resource_sort)
        query = self.resource_query.get().strip()
        if self.busy:
            return self.msg_info("任务进行中", "请等待当前任务完成后再操作。")
        def task(report):
            result = self.mods.search_page(query, kind, version, loader, category, sort, page, 20)
            result["kind"] = kind
            return result
        self.work("搜索 Modrinth 资源", task, self.got_resources)

    def got_resources(self, result):
        hits = result["hits"]
        self.resource_hits = hits
        self.resource_results_kind = result.get("kind", self._resource_kind())
        self.resource_total_hits = result["total_hits"]
        self.resource_page_number = result["page"]
        self.resource_tree.delete(*self.resource_tree.get_children())
        for index, project in enumerate(hits):
            self.resource_tree.insert("", "end", iid=str(index), values=(project.get("title", ""),
                  project.get("author", ""), f"{project.get('downloads', 0):,}", self._list_version(project)))
        self._update_pager("resource", result)

    def _update_pager(self, prefix, result):
        page = result["page"]
        limit = result["limit"]
        total = result["total_hits"]
        last = max(1, (total + limit - 1) // limit)
        getattr(self, prefix + "_page_label").set(
            f"{self.tr('第')} {page} / {last} {self.tr('页')} · {self.tr('共')} {total:,} {self.tr('条资源')}")
        getattr(self, prefix + "_prev").configure(state="normal" if page > 1 else "disabled")
        getattr(self, prefix + "_next").configure(state="normal" if page * limit < total else "disabled")

    def selected_hit(self, tree, hits):
        selected = tree.selection()
        if not selected:
            raise ValueError("请先选择一个搜索结果")
        return hits[int(selected[0])]

    def install_resource(self):
        try:
            project = self.selected_hit(self.resource_tree, self.resource_hits)
            profile = self.selected_profile(self.resource_profile)
        except ValueError as exc:
            self.msg_info("请选择资源", str(exc))
            return
        kind = self._resource_kind()
        if kind != self.resource_results_kind:
            self.msg_warning("搜索结果已过期", "资源类型已更改，请重新搜索后再安装。")
            return
        self.work("下载并安装资源", lambda report: self.mods.install_resource(project["project_id"], kind, profile, report),
                  lambda paths: self.msg_info("安装完成", "已安装文件：\n" + "\n".join(p.name for p in paths)))

    def show_resource_details(self):
        try:
            project = self.selected_hit(self.resource_tree, self.resource_hits)
        except ValueError as exc:
            return self.msg_info("请选择资源", str(exc))
        self._fetch_project_details(project, self._resource_kind(), self.resource_version.get().strip(),
                                    self._search_loader(self.resource_loader))

    def show_pack_details(self):
        try:
            project = self.selected_hit(self.pack_tree, self.pack_hits)
        except ValueError as exc:
            return self.msg_info("选择整合包", str(exc))
        self._fetch_project_details(project, "modpack", self.pack_version.get().strip(),
                                    self._search_loader(self.pack_loader))

    def _fetch_project_details(self, hit, kind, version, loader):
        def task(report):
            info = self.mods.project(hit["project_id"])
            versions = self.mods.project_versions(hit["project_id"], kind, version, loader)
            return info, versions
        self.work("获取资源详情", task,
                  lambda data: self._render_project_details(hit, kind, data[0], data[1]))

    def _render_project_details(self, hit, kind, project, versions):
        top = tk.Toplevel(self)
        top.title(project.get("title") or hit.get("title") or "Modrinth")
        top.geometry("730x610")
        top.configure(bg=BG)
        self.label(top, project.get("title", ""), size=16, color=GREEN, bold=True,
                   wraplength=670, justify="left").pack(anchor="w", padx=22, pady=(20, 5))
        meta = f"{self.tr('作者')}: {hit.get('author', '')}    ·    {self.tr('下载量')}: {project.get('downloads', 0):,}"
        self.label(top, meta, size=10, color=MUTED).pack(anchor="w", padx=22)
        license_obj = project.get("license") or {}
        categories = ", ".join(project.get("categories") or [])
        self.label(top, f"{self.tr('分类')}: {categories}    ·    License: {license_obj.get('id', 'unknown')}",
                   size=9, color=MUTED, wraplength=670, justify="left").pack(anchor="w", padx=22, pady=(8, 5))
        details = scrolledtext.ScrolledText(top, bg=PANEL, fg=TEXT, insertbackground=TEXT,
                   relief="flat", wrap="word", font=(UI_FONT, 10), height=14)
        details.pack(fill="both", expand=True, padx=22, pady=(6, 10))
        description = project.get("description") or hit.get("description") or ""
        body = project.get("body") or ""
        vers = "\n".join(f"  · {v.get('version_number','?')}  [{', '.join(v.get('game_versions') or [])}]  "
                         f"({', '.join(v.get('loaders') or [])})" for v in versions[:15])
        content = f"{description}\n\n{body[:6500]}\n\n{self.tr('可用版本')} ({len(versions)}):\n{vers}"
        details.insert("1.0", content)
        details.configure(state="disabled")
        row = tk.Frame(top, bg=BG)
        row.pack(fill="x", padx=22, pady=(0, 18))
        path = project.get("slug") or project.get("id") or hit["project_id"]
        self.button(row, "查看项目主页", lambda: webbrowser.open("https://modrinth.com/" + kind + "/" + path)).pack(side="left")
        self.button(row, "关闭", top.destroy).pack(side="right")
        self.translate_ui()

    def import_resource(self):
        try:
            profile = self.selected_profile(self.resource_profile)
        except ValueError as exc:
            self.msg_info("选择实例", str(exc))
            return
        kind = self._resource_kind()
        type_desc = [(self.tr("Minecraft Mod"), "*.jar")] if kind == "mod" else [(self.tr("ZIP 资源文件"), "*.zip")]
        file = filedialog.askopenfilename(title=self.tr("选择资源文件"), filetypes=type_desc + [(self.tr("所有文件"), "*.*")])
        if file:
            try:
                target = copy_local_resource(Path(file), kind, profile)
                self.msg_info("已导入", str(target))
            except Exception as exc:
                self.msg_error("导入失败", str(exc))

    def open_resource_page(self):
        try:
            hit = self.selected_hit(self.resource_tree, self.resource_hits)
            webbrowser.open("https://modrinth.com/" + self._resource_kind() + "/" + hit.get("slug", hit["project_id"]))
        except ValueError as exc:
            self.msg_info("选择资源", str(exc))

    def search_packs(self, page=1):
        cat_values = list(self._CATEGORY_NAMES["modpack"].values())
        category = cat_values[max(0, self.pack_category.current())]
        query = self.pack_query.get().strip()
        version = self.pack_version.get().strip()
        loader = self._search_loader(self.pack_loader)
        sort = self._search_sort(self.pack_sort)
        if self.busy:
            return self.msg_info("任务进行中", "请等待当前任务完成后再操作。")
        self.work("搜索 Modrinth 整合包", lambda report: self.mods.search_page(
            query, "modpack", version, loader, category, sort, page, 20), self.got_packs)

    def got_packs(self, result):
        hits = result["hits"]
        self.pack_hits = hits
        self.pack_page_number = result["page"]
        self.pack_total_hits = result["total_hits"]
        self.pack_tree.delete(*self.pack_tree.get_children())
        for i, hit in enumerate(hits):
            self.pack_tree.insert("", "end", iid=str(i), values=(hit.get("title", ""),
                      hit.get("author", ""), f"{hit.get('downloads', 0):,}", self._list_version(hit)))
        self._update_pager("pack", result)

    def install_pack(self):
        try:
            project = self.selected_hit(self.pack_tree, self.pack_hits)
        except ValueError as exc:
            self.msg_info("选择整合包", str(exc))
            return
        if not self.msg_askyesno("安装整合包", "即将下载整合包及其依赖文件。安装过程可能较长，是否继续？"):
            return

        version_filter = self.pack_version.get().strip()
        loader_filter = self._search_loader(self.pack_loader)

        def task(report):
            versions = self.mods.project_versions(project["project_id"], "modpack",
                    version_filter, loader_filter)
            if not versions:
                raise RuntimeError("这个整合包没有符合所选游戏版本和加载器的可用版本")
            vfile = self.mods.primary_file(versions[0], ".mrpack")
            cache = self.config_model.path.parent / "downloads"
            path = self.mods.download(vfile, cache, report)
            return self.mc.install_mrpack(path, project["title"], report)

        self.work("安装整合包", task, lambda profile: (self.log("已创建：" + profile["name"]),
                                                    self.switch_page("首页")))

    def import_pack(self):
        file = filedialog.askopenfilename(title=self.tr("选择 Modrinth 整合包"), filetypes=[(self.tr("Modrinth 整合包"), "*.mrpack")])
        if file:
            self.work("导入本地整合包", lambda report: self.mc.install_mrpack(Path(file), "", report),
                      lambda p: (self.log("已导入：" + p["name"]), self.switch_page("首页")))

    def open_pack_page(self):
        try:
            hit = self.selected_hit(self.pack_tree, self.pack_hits)
            webbrowser.open("https://modrinth.com/modpack/" + hit.get("slug", hit["project_id"]))
        except ValueError as exc:
            self.msg_info("选择整合包", str(exc))

    def browse_setting(self, key, mode):
        if mode == "dir":
            file = filedialog.askdirectory(title=self.tr("选择 Minecraft 安装目录"))
        elif mode == "image":
            file = filedialog.askopenfilename(title=self.tr("选择启动器背景图"),
                    filetypes=[(self.tr("图片"), "*.png *.jpg *.jpeg *.webp"), (self.tr("所有文件"), "*.*")])
        else:
            file = filedialog.askopenfilename(title=self.tr("选择 Java 可执行文件"), filetypes=[(self.tr("Java"), "java.exe javaw.exe *"), (self.tr("所有文件"), "*.*")])
        if file:
            self.setting_inputs[key].delete(0, "end")
            self.setting_inputs[key].insert(0, file)

    def request_python_update(self):
        """Ask the Windows EXE bootstrapper to check on the *next* launch.

        No global Python, Java or game files are modified here. Running Python
        cannot safely be replaced while the GUI is active.
        """
        if os.name != "nt" or not os.environ.get("LOCALAPPDATA"):
            self.msg_info("Python 更新", "此功能只适用于 Windows EXE 引导版。")
            return
        marker = Path(os.environ["LOCALAPPDATA"]) / "BlockCraftLauncher" / "python-runtime" / "last_check.txt"
        try:
            marker.unlink(missing_ok=True)
        except OSError as exc:
            self.msg_error("Python 更新", f"无法安排更新检查：{exc}")
            return
        self.msg_info("Python 更新", "已安排：下次打开 Windows EXE 时会检查 Python 更新。")

    def save_settings(self):
        d = self.config_model.data
        for key, widget in self.setting_inputs.items():
            d[key] = widget.get().strip()
        d["mirror"] = self.use_mirror.get()
        d["download_threads"] = max(2, min(32, int(self.download_threads.get())))
        d["memory_mb"] = self.memory_var.get()
        self.config_model.save()
        self.render_hero()
        self.log("设置已保存（Minecraft 目录与登录配置对后续操作生效）")
        self.msg_info("设置已保存", "修改已保存。背景会立即更新。\n下载设置仅影响后续下载任务。")

    def close_app(self):
        if self.busy and not self.msg_askyesno("任务仍在进行", "当前有下载或安装任务，退出会中断操作。确定退出吗？"):
            return
        self.auth.cancel_browser()
        self.auth.cancel_device()
        self.destroy()


if __name__ == "__main__":
    LauncherApp().mainloop()
