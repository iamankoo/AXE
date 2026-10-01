-- AXE v2 per-installation device keys.
--
-- Each AXE installation generates its own ECDSA P-256 key pair; the device id is the SHA-256 of its public key. The public
-- key is registered with the request and copied onto the grant, so the server can require a signature by the matching private
-- key on every authorization check (a copied grant is useless on another installation).
--
-- It is public information (a verification key), not a secret, and lives only as long as the request / grant row does.

alter table public.access_requests add column if not exists device_pubkey text;
alter table public.access_grants   add column if not exists device_pubkey text;
