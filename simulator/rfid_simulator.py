#!/usr/bin/env python3
import argparse, json, uuid, urllib.request, urllib.error
from datetime import datetime, timezone

def post(url,token,payload):
 req=urllib.request.Request(url,data=json.dumps(payload).encode(),headers={"Content-Type":"application/json","Authorization":f"Bearer {token}"},method="POST")
 try:
  with urllib.request.urlopen(req,timeout=5) as r:
   body=json.loads(r.read().decode()); print(f"HTTP {r.status}\n{json.dumps(body,indent=2)}"); return r.status
 except urllib.error.HTTPError as e:
  print(f"HTTP {e.code}\n{e.read().decode(errors='replace')}"); return e.code
 except urllib.error.URLError as e:
  print(f"API inaccessible: {e.reason}. Aucune décision serveur confirmée."); return None

def main():
 p=argparse.ArgumentParser(description="Terminal RFID virtuel sans matériel")
 p.add_argument("--base-url",default="http://localhost:5080");p.add_argument("--token",default="dev-terminal-token-change-me")
 p.add_argument("--uid",default="04A1B2C3D4E5F6");p.add_argument("--event-id",default=None)
 p.add_argument("--mode",choices=["normal","replay","conflict","revoked","unknown","bad-token"],default="normal")
 a=p.parse_args(); eid=a.event_id or str(uuid.uuid4()); uid=a.uid; token=a.token
 if a.mode=="revoked":uid="04DEAD00000001"
 if a.mode=="unknown":uid="04FFFFFFFFFFFF"
 if a.mode=="bad-token":token="invalid-token"
 payload={"eventId":eid,"cardUid":uid,"occurredAt":datetime.now(timezone.utc).isoformat(),"eventType":"TRANSPORT_VALIDATION"}
 url=a.base_url.rstrip("/")+"/api/validations"
 print(f"Terminal virtuel → {url}\neventId={eid}, cardUid={uid}, mode={a.mode}")
 status=post(url,token,payload)
 if status is not None and a.mode=="replay":
  print("\nRejeu exact :");post(url,token,payload)
 elif status is not None and a.mode=="conflict":
  print("\nMême eventId, autre UID :");changed=dict(payload);changed["cardUid"]="04DEAD00000001";post(url,token,changed)
if __name__=="__main__":main()
