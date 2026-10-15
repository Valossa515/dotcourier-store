#!/usr/bin/env bash
# End-to-end check of the Store API: every DotCourier feature through real HTTP calls.
# Needs a freshly started API in Development (the database is reset on start).
# Usage: scripts/smoke.sh [base-url] [api-log-file]
BASE="${1:-http://localhost:5199}"
LOG="${2:-}"
BODY_WIDTH="${BODY_WIDTH:-170}"
PASS=0; FAIL=0

check() { # name expected actual body
  if [ "$2" = "$3" ]; then PASS=$((PASS+1)); m="✅"; else FAIL=$((FAIL+1)); m="❌"; fi
  printf '%s %-58s expected %s got %s\n' "$m" "$1" "$2" "$3"
  if [ "$BODY_WIDTH" = "0" ]; then printf '      %s\n' "$4"; else printf '      %s\n' "$(echo "$4" | cut -c1-"$BODY_WIDTH")"; fi
}
has() { # name pattern text
  if echo "$3" | grep -q -- "$2"; then PASS=$((PASS+1)); printf '✅ %s\n' "$1"; else FAIL=$((FAIL+1)); printf '❌ %s (missing %s)\n' "$1" "$2"; fi
}
call() { # method path [json] [extra curl args...]
  local method="$1" path="$2" json="${3:-}"; shift 3 2>/dev/null || shift $#
  if [ -n "$json" ]; then
    curl -s -o /tmp/st_body -w '%{http_code}' -X "$method" "$BASE$path" -H 'Content-Type: application/json' -d "$json" "$@"
  else
    curl -s -o /tmp/st_body -w '%{http_code}' -X "$method" "$BASE$path" "$@"
  fi
}
body() { cat /tmp/st_body; }
# First Python that actually runs (on Windows "python3" can be a Microsoft Store stub that only prints a message).
PY=""
for candidate in python3 python; do
  if "$candidate" -c "import sys" >/dev/null 2>&1; then PY="$candidate"; break; fi
done
[ -n "$PY" ] || { echo "Python is required"; exit 1; }
json() { "$PY" -c "import sys,json; d=json.load(sys.stdin); print($1)" | tr -d '\r'; } # Windows Pythons may print CRLF


PRODUCTS=$(curl -s "$BASE/api/products")
KB=$(echo "$PRODUCTS" | json "[p['id'] for p in d['data'] if p['sku']=='KB-001'][0]")
MS=$(echo "$PRODUCTS" | json "[p['id'] for p in d['data'] if p['sku']=='MS-001'][0]")
HS=$(echo "$PRODUCTS" | json "[p['id'] for p in d['data'] if p['sku']=='HS-001'][0]")

echo "== 1. Catálogo: validação, cache e invalidação =="
s=$(call GET "/api/products/$KB"); check "GET produto (1ª vez roda o handler)" 200 "$s" "$(body)"
s=$(call GET "/api/products/$KB"); check "GET produto de novo (vem do cache)" 200 "$s" "$(body)"
[ -n "$LOG" ] && has "  ...log mostra 'Cache hit for GetProduct'" 'Cache hit for GetProduct' "$(cat "$LOG")"
s=$(call GET "/api/products/11111111-1111-1111-1111-111111111111"); b=$(body); check "produto inexistente -> 404" 404 "$s" "$b"; has "  ...com código PRD-404" '"errorCode":"PRD-404"' "$b"
s=$(call POST /api/products '{"name":"","sku":"x","price":0,"stock":-1}'); b=$(body); check "criar produto inválido -> 400 (FluentValidation)" 400 "$s" "$b"; has "  ...códigos por campo" '"code":"PRD-PRICE"' "$b"
s=$(call POST /api/products '{"name":"Teclado clone","sku":"KB-001","price":10,"stock":1}'); b=$(body); check "SKU duplicado -> 409" 409 "$s" "$b"; has "  ...com código PRD-SKU-TAKEN" 'PRD-SKU-TAKEN' "$b"
s=$(call POST /api/products '{"name":"USB-C hub","sku":"HUB-007","price":149.9,"stock":12}'); check "criar produto válido -> 201" 201 "$s" "$(body)"
s=$(call PUT "/api/products/$KB/price" '{"newPrice":0}'); check "preço inválido -> 400" 400 "$s" "$(body)"
s=$(call PUT "/api/products/$KB/price" '{"newPrice":199.9}'); check "trocar preço -> 200 (invalida o cache)" 200 "$s" "$(body)"
s=$(call GET "/api/products/$KB"); b=$(body); check "GET produto depois da troca" 200 "$s" "$b"; has "  ...já com o preço novo" '"price":199.9' "$b"

echo; echo "== 1b. Transação: importação tudo ou nada =="
s=$(call POST /api/products/import '[{"name":"Mousepad","sku":"IMP-001","price":49.9,"stock":30},{"name":"Webcam","sku":"IMP-002","price":299,"stock":8}]'); b=$(body); check "importar 2 produtos -> 201" 201 "$s" "$b"; has "  ...os dois criados" '"sku":"IMP-002"' "$b"
s=$(call POST /api/products/import '[{"name":"Microfone","sku":"IMP-003","price":199,"stock":4},{"name":"Teclado clone","sku":"KB-001","price":10,"stock":1}]'); b=$(body); check "importar com SKU repetido no 2º item -> 409" 409 "$s" "$b"; has "  ...aponta o item e o código" 'Item 2: .*PRD-SKU-TAKEN' "$b"
s=$(call POST /api/products '{"name":"Microfone","sku":"IMP-003","price":199,"stock":4}'); check "  ...o 1º item foi desfeito: criar IMP-003 de novo -> 201" 201 "$s" "$(body)"
s=$(call POST /api/products/import '[{"name":"Caixa de som","sku":"IMP-004","price":159,"stock":6},{"name":"","sku":"IMP-005","price":0,"stock":1}]'); b=$(body); check "importar com item inválido -> 400 (validação do item)" 400 "$s" "$b"
s=$(call POST /api/products '{"name":"Caixa de som","sku":"IMP-004","price":159,"stock":6}'); check "  ...IMP-004 também foi desfeito -> 201" 201 "$s" "$(body)"

echo; echo "== 2. Pedidos: idempotência e regras de domínio =="
ORDER_JSON="{\"customerEmail\":\"ana@example.com\",\"items\":[{\"productId\":\"$KB\",\"quantity\":2}]}"
s=$(call POST /api/orders "$ORDER_JSON"); b=$(body); check "pedido sem Idempotency-Key -> 400" 400 "$s" "$b"; has "  ...código ORD-KEY" 'ORD-KEY' "$b"
s=$(call POST /api/orders "$ORDER_JSON" -H 'Idempotency-Key: k-001'); b1=$(body); check "fazer pedido -> 201" 201 "$s" "$b1"
ORDER1=$(echo "$b1" | json "d['data']['id']")
s=$(call POST /api/orders "$ORDER_JSON" -H 'Idempotency-Key: k-001'); b2=$(body); check "mesmo pedido, mesma chave -> 201" 201 "$s" "$b2"
if [ "$(echo "$b2" | json "d['data']['id']")" = "$ORDER1" ]; then PASS=$((PASS+1)); echo "✅   ...mesmo pedido devolvido, sem duplicar"; else FAIL=$((FAIL+1)); echo "❌   ...pedido duplicado"; fi
s=$(call GET "/api/products/$KB"); b=$(body); has "  ...estoque baixou só uma vez (10 -> 8)" '"stock":8' "$b"
MS_JSON="{\"customerEmail\":\"bia@example.com\",\"items\":[{\"productId\":\"$MS\",\"quantity\":1}]}"
for i in 1 2 3 4; do curl -s -X POST "$BASE/api/orders" -H 'Content-Type: application/json' -H 'Idempotency-Key: k-002' -d "$MS_JSON" > "/tmp/st_par_$i" & done; wait
u=$(for i in 1 2 3 4; do json "d['data']['id']" < "/tmp/st_par_$i"; done | sort -u | wc -l)
if [ "$u" = "1" ]; then PASS=$((PASS+1)); echo "✅ 4 pedidos simultâneos com a mesma chave -> 1 pedido só"; else FAIL=$((FAIL+1)); echo "❌ 4 pedidos simultâneos geraram $u pedidos"; fi
ORDER2=$(json "d['data']['id']" < /tmp/st_par_1)
s=$(call POST /api/orders "{\"customerEmail\":\"caio@example.com\",\"items\":[{\"productId\":\"$HS\",\"quantity\":1}]}" -H 'Idempotency-Key: k-003'); b=$(body); check "produto sem estoque -> 409 (regra de domínio)" 409 "$s" "$b"; has "  ...código PRODUCT_OUT_OF_STOCK" 'PRODUCT_OUT_OF_STOCK' "$b"
s=$(call POST /api/orders "{\"customerEmail\":\"caio@example.com\",\"items\":[{\"productId\":\"11111111-1111-1111-1111-111111111111\",\"quantity\":1}]}" -H 'Idempotency-Key: k-004'); check "produto inexistente no pedido -> 404" 404 "$s" "$(body)"

echo; echo "== 3. Pagamento: retry e circuit breaker =="
s=$(call PUT "/api/simulation/payment-gateway?mode=Flaky&failures=2"); check "gateway instável (2 falhas)" 200 "$s" "$(body)"
s=$(call POST "/api/orders/$ORDER1/pay"); b=$(body); check "pagar -> 200 (o retry absorveu as falhas)" 200 "$s" "$b"; has "  ...status Paid" '"status":"Paid"' "$b"
s=$(curl -s "$BASE/api/simulation/payment-gateway"); has "  ...gateway chamado 3 vezes" '"attempts":3' "$s"
s=$(call POST "/api/orders/$ORDER1/pay"); b=$(body); check "pagar de novo -> 409" 409 "$s" "$b"; has "  ...código ORDER_NOT_PAYABLE" 'ORDER_NOT_PAYABLE' "$b"
s=$(call POST "/api/orders/$ORDER1/cancel"); b=$(body); check "cancelar pedido pago -> 409" 409 "$s" "$b"; has "  ...código ORDER_ALREADY_PAID" 'ORDER_ALREADY_PAID' "$b"
s=$(call POST "/api/orders/$ORDER2/cancel"); b=$(body); check "cancelar pedido pendente -> 200" 200 "$s" "$b"; has "  ...status Cancelled" '"status":"Cancelled"' "$b"
s=$(call PUT "/api/simulation/payment-gateway?mode=Down"); check "gateway fora do ar" 200 "$s" "$(body)"
O3=$(curl -s -X POST "$BASE/api/orders" -H 'Content-Type: application/json' -H 'Idempotency-Key: k-005' -d "$MS_JSON" | json "d['data']['id']")
# The breaker opens at 50% failures over at least 3 calls. PayOrder already had 2 calls that did not
# fail (the payment and the 409), so 2 failures make 2 of 4 = 50% and the 3rd attempt is rejected.
for i in 1 2; do s=$(call POST "/api/orders/$O3/pay" "" -H 'Accept-Language: pt-BR'); b=$(body); check "pagar com gateway fora ($i/2) -> 500 (após 3 tentativas)" 500 "$s" "$b"; done
has "  ...mensagem genérica em pt-BR, código 000" 'Ocorreu um erro inesperado.*"errorCode":"000"' "$b"
ATTEMPTS=$(curl -s "$BASE/api/simulation/payment-gateway" | json "d['attempts']")
s=$(call POST "/api/orders/$O3/pay" "" -H 'Accept-Language: pt-BR'); b=$(body); check "3ª tentativa -> 503 (circuito aberto: 50% de falhas)" 503 "$s" "$b"; has "  ...mensagem em pt-BR, código CIRCUIT-OPEN" 'temporariamente indispon.*CIRCUIT-OPEN' "$b"
check "  ...o gateway nem foi chamado" "$ATTEMPTS" "$(curl -s "$BASE/api/simulation/payment-gateway" | json "d['attempts']")" "tentativas no gateway: $ATTEMPTS"
s=$(call PUT "/api/simulation/payment-gateway?mode=Healthy"); check "gateway de volta" 200 "$s" "$(body)"

echo; echo "== 4. Relatório: timeout e bulkhead =="
s=$(call GET /api/reports/sales); b=$(body); check "relatório de vendas -> 200" 200 "$s" "$b"; has "  ...1 pedido pago" '"paidOrders":1' "$b"
s=$(call GET "/api/reports/sales?simulatedDelayMs=5000" "" -H 'Accept-Language: pt-BR'); b=$(body); check "relatório lento -> 504 (timeout de 3 s)" 504 "$s" "$b"; has "  ...mensagem em pt-BR com o tempo" 'demorou mais de 3000 ms' "$b"
curl -s -o /tmp/st_rep1 -w '%{http_code}' "$BASE/api/reports/sales?simulatedDelayMs=1500" > /tmp/st_rep1_status &
sleep 0.3
s=$(call GET /api/reports/sales); b=$(body); check "2º relatório ao mesmo tempo -> 503 (bulkhead 1)" 503 "$s" "$b"; has "  ...código BUSY" '"errorCode":"BUSY"' "$b"
wait; check "o 1º relatório termina normalmente" 200 "$(cat /tmp/st_rep1_status)" "$(cat /tmp/st_rep1)"

echo; echo "== 5. Rate limit da listagem =="
sleep 11 # new 10 s window
for i in 1 2 3 4 5; do s=$(call GET "/api/products?search=termo$i"); check "listagem $i/5 (busca nova, sem cache)" 200 "$s" "$(body)"; done
# Fixed 10 s windows on their own timer: the 5 calls above may straddle a reset, so a single 6th call
# can still get a permit. 6 more calls in a burst span at most one reset: at least one must be refused.
s=200; b=""
for i in 6 7 8 9 10 11; do s=$(call GET "/api/products?search=termo$i" "" -H 'Accept-Language: pt-BR'); b=$(body); [ "$s" = 429 ] && break; done
check "listagens além do limite -> 429" 429 "$s" "$b"; has "  ...mensagem em pt-BR, código RATE-LIMITED" 'Muitas requisi.*RATE-LIMITED' "$b"
s=$(call GET "/api/products?search=termo1"); check "busca repetida vem do cache e não é barrada" 200 "$s" "$(body)"

echo; echo "== 6. Mensagens no idioma do request =="
s=$(call POST /api/products '{"name":"","sku":"","price":0,"stock":0}' -H 'Accept-Language: pt-BR'); b=$(body); check "validação em pt-BR" 400 "$s" "$b"; has "  ...resumo traduzido com a contagem" 'campo(s) inv' "$b"; has "  ...código VAL-000" '"errorCode":"VAL-000"' "$b"
s=$(call POST /api/products '{"name":"","sku":"","price":0,"stock":0}' -H 'Accept-Language: en-US'); b=$(body); check "validação em en-US" 400 "$s" "$b"; has "  ...resumo em inglês" 'invalid field' "$b"

echo; echo "== 7. Outbox: eventos gravados com os dados e entregues em background =="
# Delivery is asynchronous: wait (up to 15 s) until nothing is pending.
for i in $(seq 1 30); do OB=$(curl -s "$BASE/api/simulation/outbox"); [ "$(echo "$OB" | json "d['pending']+d['processing']")" = "0" ] && break; sleep 0.5; done
check "outbox sem mensagens pendentes" 0 "$(echo "$OB" | json "d['pending']+d['processing']")" "$OB"
check "  ...nenhuma mensagem falhou" 0 "$(echo "$OB" | json "d['failed']")" "$OB"
PROCESSED=$(echo "$OB" | json "d['processed']")
if [ "$PROCESSED" -gt 0 ]; then PASS=$((PASS+1)); echo "✅   ...$PROCESSED eventos entregues"; else FAIL=$((FAIL+1)); echo "❌   ...nenhum evento entregue"; fi

if [ -n "$LOG" ]; then
  echo; echo "== 8. Eventos de domínio e notificações (log) =="
  has "processador do outbox iniciou" 'Outbox processor started for StoreDbContext' "$(cat "$LOG")"
  L=$(cat "$LOG")
  has "e-mail de confirmação do pedido" 'We received your order' "$L"
  has "e-mail de pagamento confirmado" 'Payment confirmed' "$L"
  has "auditoria genérica recebeu OrderPlaced" 'DomainEventNotification<OrderPlaced>' "$L"
  has "auditoria genérica recebeu OrderPaid" 'DomainEventNotification<OrderPaid>' "$L"
  has "circuito abriu para PayOrder" 'Circuit opened for PayOrder' "$L"
  has "transação confirmada para ImportProducts" 'Transaction committed for ImportProducts' "$L"
  has "rollback por resposta de erro (SKU repetido)" 'Transaction rolled back for ImportProducts (error response)' "$L"
  has "rollback por exceção (item inválido)" 'Transaction rolled back for ImportProducts (exception)' "$L"
  # 4 seeded + HUB-007 + IMP-001/002 + IMP-003/004 created again; the 2 rolled-back imports store no event.
  CREATED=$(echo "$L" | grep -c 'DomainEventNotification<ProductCreated>')
  check "ProductCreated só dos commits (imports desfeitos não gravam evento)" 9 "$CREATED" "eventos ProductCreated no log: $CREATED"
  if echo "$L" | grep -q "@example.com"; then FAIL=$((FAIL+1)); echo "❌ e-mail de cliente apareceu no log"; else PASS=$((PASS+1)); echo "✅ nenhum e-mail de cliente no log"; fi
fi

echo; echo "Result: $PASS passed, $FAIL failed"
[ "$FAIL" -eq 0 ]
