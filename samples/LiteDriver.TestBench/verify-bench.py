#!/usr/bin/env python3
"""测试台端到端验证：逐协议启动双驱动并断言连通测试全通过。"""
import json
import sys
import urllib.request

BASE = "http://127.0.0.1:8099"
PROTOCOLS = ["length-prefix", "line-terminator", "fixed-length", "stx-etx", "modbus-like"]


def post(path, payload):
    data = json.dumps(payload).encode()
    req = urllib.request.Request(
        BASE + path, data=data, headers={"Content-Type": "application/json"}, method="POST"
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.load(resp)


def main():
    failures = 0
    for proto in PROTOCOLS:
        print(f"===== {proto}")
        start = post("/api/start", {
            "clientProtocolId": proto,
            "serverProtocolId": proto,
            "port": 0,
        })
        if not start.get("ok"):
            print(f"  FAIL 启动失败: {start.get('error')}")
            failures += 1
            continue

        status = start["data"]
        print(f"  服务端端口={status['serverPort']} 客户端={status['clientState']}")

        report = post("/api/connectivity", {"message": "ci-probe"})["data"]
        for step in report["steps"]:
            mark = "PASS" if step["passed"] else "FAIL"
            print(f"  {mark} {step['name']} | {step['detail']}")
        if not report["passed"]:
            failures += 1
        else:
            print(f"  响应={report.get('responseText')!r}")

    if failures:
        print(f"::error::{failures} 个协议连通测试未通过")
        return 1
    print("ALL PROTOCOLS PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
