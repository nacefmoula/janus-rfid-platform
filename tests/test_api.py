#!/usr/bin/env python3
import json, os, unittest, urllib.request, urllib.error, uuid
from datetime import datetime, timezone
BASE=os.environ.get("JANUS_BASE_URL",os.environ.get("RFID_BASE_URL","http://localhost:5080"))
TOKEN=os.environ.get("TERMINAL_DEMO_TOKEN","dev-terminal-token-change-me")
def call(path,method="GET",payload=None,token=TOKEN):
 h={}
 if token is not None:h["Authorization"]=f"Bearer {token}"
 data=None
 if payload is not None:data=json.dumps(payload).encode();h["Content-Type"]="application/json"
 req=urllib.request.Request(BASE+path,data=data,headers=h,method=method)
 try:
  with urllib.request.urlopen(req,timeout=5) as r:return r.status,json.loads(r.read().decode())
 except urllib.error.HTTPError as e:
  raw=e.read().decode()
  try:body=json.loads(raw)
  except ValueError:body=raw
  return e.code,body
def event(uid="04A1B2C3D4E5F6"):
 return {"eventId":str(uuid.uuid4()),"cardUid":uid,"occurredAt":datetime.now(timezone.utc).isoformat(),"eventType":"TRANSPORT_VALIDATION"}
class ApiTests(unittest.TestCase):
 def test_health(self):
  s,b=call("/health");self.assertEqual(s,200);self.assertEqual(b["status"],"healthy");self.assertEqual(b["service"],"janus-api")
 def test_active_card(self):
  s,b=call("/api/validations","POST",event());self.assertEqual(s,200);self.assertEqual(b["decision"],"ACCEPTED");self.assertFalse(b["duplicate"])
 def test_exact_replay(self):
  p=event();s1,_=call("/api/validations","POST",p);s2,b2=call("/api/validations","POST",p)
  self.assertEqual((s1,s2),(200,200));self.assertTrue(b2["duplicate"])
 def test_changed_payload_conflicts(self):
  p=event();s1,_=call("/api/validations","POST",p);p2=dict(p);p2["cardUid"]="04DEAD00000001";s2,b2=call("/api/validations","POST",p2)
  self.assertEqual(s1,200);self.assertEqual(s2,409);self.assertEqual(b2["error"],"idempotency_conflict")
 def test_revoked_card(self):
  s,b=call("/api/validations","POST",event("04DEAD00000001"));self.assertEqual(s,200);self.assertEqual(b["decision"],"REJECTED");self.assertEqual(b["reason"],"CARD_REVOKED")
 def test_unknown_card(self):
  s,b=call("/api/validations","POST",event("04FFFFFFFFFFFF"));self.assertEqual(s,200);self.assertEqual(b["reason"],"UNKNOWN_CARD")
 def test_bad_token(self):
  s,_=call("/api/validations","POST",event(),token="wrong");self.assertEqual(s,401)
 def test_client_cannot_choose_terminal_identity(self):
  p=event();p["terminalId"]="ATTACKER";s,b=call("/api/validations","POST",p);self.assertEqual(s,200);self.assertEqual(b["terminalId"],"TERMINAL-DEMO-01")
if __name__=="__main__":unittest.main(verbosity=2)
