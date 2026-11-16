#!/usr/bin/env python3
"""编排服务端到端验证：热应用、端口回写、客户端连通、增删节点，全程不重启进程。"""
import json
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:8110"


def call(path, payload=None):
    if payload is None:
        req = urllib.request.Request(BASE + path)
    else:
        req = urllib.request.Request(
            BASE + path, data=json.dumps(payload).encode(),
            headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=30) as resp:
        body = json.load(resp)
    if not body.get("ok"):
        raise RuntimeError(f"{path} 失败: {body.get('error')}")
    return body["data"]


def fail(msg):
    print(f"  FAIL {msg}")
    return 1


def main():
    failures = 0

    # 1) 协议目录
    protocols = call("/api/protocols")
    print(f"协议目录 {len(protocols)} 项: {[p['kindId'] for p in protocols]}")
    if len(protocols) < 4:
        failures += fail("协议数量不足")

    # 2) 首次应用（服务端 Port=0）
    graph = {
        "revision": 0,
        "nodes": [
            {"id": "p", "kind": "Protocol", "label": "协议", "x": 60, "y": 150, "kindId": "length-prefix"},
            {"id": "srv", "kind": "Server", "label": "服务端", "x": 340, "y": 240,
             "host": "127.0.0.1", "port": 0, "maxSessions": 8},
            {"id": "cli", "kind": "Client", "label": "客户端", "x": 340, "y": 60,
             "endpoints": ["127.0.0.1:1"]},
        ],
        "edges": [{"id": "e1", "from": "p", "to": "srv"}, {"id": "e2", "from": "p", "to": "cli"}],
    }
    r1 = call("/api/apply", graph)
    if not r1["ok"]:
        failures += fail(f"首次应用失败: {r1['issues']}")
        print(json.dumps({"failures": failures}))
        return failures

    time.sleep(2)
    port = [n for n in call("/api/graph")["nodes"] if n["kind"] == "Server"][0]["port"]
    print(f"  服务端实际端口={port}（已回写进图）")
    if port <= 0:
        failures += fail("端口未回写")

    # 3) 校验：未连协议的节点必须被拒
    bad = json.loads(json.dumps(graph))
    bad["nodes"].append({"id": "orphan", "kind": "Server", "label": "孤立", "host": "127.0.0.1", "port": 0})
    v = call("/api/validate", bad)
    if v["ok"]:
        failures += fail("孤立服务端节点未被拒绝")
    else:
        assert any(i.get("nodeId") == "orphan" for i in v["issues"]), "问题未定位到节点"
        print(f"  校验拒绝且定位到节点: {v['issues'][0]['code']}")

    # 4) 热更新客户端指向真实端口 -> 应连通
    graph["nodes"][1]["port"] = port
    graph["nodes"][2]["endpoints"] = [f"127.0.0.1:{port}"]
    r2 = call("/api/apply", graph)
    if not r2["ok"]:
        failures += fail(f"热更新失败: {r2['issues']}")
    time.sleep(4)
    st = call("/api/state")
    print(f"  readyClients={st['readyClients']} inboundSessions={st['inboundSessions']}")
    if st["readyClients"] < 1:
        failures += fail("客户端未就绪")
    if st["inboundSessions"] < 1:
        failures += fail("服务端未收到会话")

    # 5) 运行期新增服务端节点，进程不重启
    graph["nodes"].append({"id": "srv2", "kind": "Server", "label": "服务端2",
                           "host": "127.0.0.1", "port": 0, "maxSessions": 4})
    graph["edges"].append({"id": "e3", "from": "p", "to": "srv2"})
    r3 = call("/api/apply", graph)
    if not r3["ok"]:
        failures += fail(f"新增节点失败: {r3['issues']}")
    time.sleep(2)
    st2 = call("/api/state")
    listening = [n for n in st2["nodes"] if n["kind"] == "Server" and n["port"] > 0]
    print(f"  监听端点数={len(listening)} ports={[n['port'] for n in listening]}")
    if len(listening) < 2:
        failures += fail("新增的服务端未监听")
    # 原服务端端口不应漂移
    if listening[0]["port"] != port:
        failures += fail(f"原服务端端口漂移 {port} -> {listening[0]['port']}")

    # 6) 删除第二个服务端
    graph["nodes"] = [n for n in graph["nodes"] if n["id"] != "srv2"]
    graph["edges"] = [e for e in graph["edges"] if e["to"] != "srv2"]
    r4 = call("/api/apply", graph)
    time.sleep(2)
    st3 = call("/api/state")
    remaining = [n for n in st3["nodes"] if n["kind"] == "Server"]
    print(f"  删除后监听端点数={len(remaining)}")
    if len(remaining) != 1:
        failures += fail("删除节点后监听数不正确")

    print("ALL CHECKS PASSED" if failures == 0 else f"{failures} CHECK(S) FAILED")
    return failures


if __name__ == "__main__":
    sys.exit(0 if main() == 0 else 1)
