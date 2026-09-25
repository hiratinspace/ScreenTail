# Hudu

How ScreenTail talks to Hudu (ST-095, ST-096), what a tenant supplies, and what to check against a real
Hudu before a pilot. Written from Hudu's API documentation; the real check has not been run.

## What ScreenTail does with it

| Call | Route | When |
|---|---|---|
| Check the key | `GET /api/v1/api_info` | Settings → Integrations |
| List companies | `GET /api/v1/companies?page=n&page_size=25`, until a short page; cached ten minutes per tenant | Mapping a PSA company to a Hudu one (ST-097), and before every article |
| Create an article | `POST /api/v1/articles` with `{"article": {"name", "content", "company_id", "draft": true}}` | Publish, KB destination on |
| Attach screenshots | `POST /api/v1/uploads` with `uploadable_type=Article`, `uploadable_id`, `file`, one per included frame, after the article | Publish |

Every request carries the tenant's key in the `x-api-key` header and never in a URL. A 429 or an
outage is retried with exponential backoff and jitter, three attempts at most; a wrong key is not
retried. Every refusal reaches the technician as a kind and two sentences: "Hudu rejected the API key.
Update it in Settings → Integrations." A 422 carries Hudu's own words about the field it refused.

**A company Hudu does not have is refused before anything is sent.** Publishing to the wrong company is
one customer's runbook in another customer's knowledge base, so the company id must be one of the
tenant's.

## What the tenant supplies

1. **An API key**: Hudu → Admin → API Keys → New. Give it the permissions to read companies and to
   create articles and uploads; nothing more.
2. **The site**: the tenant's Hudu address, for example `https://acme.huducloud.com`.

Stored once, over the device's authenticated connection to the backend, and never returned:

```bash
curl -X PUT "$BACKEND/v1/integrations/hudu" \
  -H "Authorization: Bearer $SCREENTAIL_DEVICE_TOKEN" -H "Content-Type: application/json" \
  -d '{"siteUrl":"https://acme.huducloud.com","secret":"<the API key>"}'
```

## Images: what to verify first

**Articles are created as drafts**, so a model-written article is never live before a person reads it.
The `draft` field on `POST /api/v1/articles` is the thing to confirm against a real Hudu before anything
else: if that Hudu ignores it, the article is created published, which is the one outcome this
integration must not have. Create one article in a test company and open it in Hudu; it must say Draft.

**Screenshots are uploads on the article**, not images inside its body. Hudu's article body is HTML and
an image in it needs a URL Hudu serves; an upload against the article is what Hudu shows under the
article's attachments, which is the fallback ST-096 AC3 asks for. When the real check confirms that an
upload's URL can be referenced from the body, the provider can add `<img>` tags; until then the
attachments are the images.

## The real check

Needs a Hudu instance with an API key, on the owner's list. When there is one:

1. Store the key as above.
2. `GET /v1/integrations` shows `hudu` with the last four characters of the key.
3. Publish a session with the KB destination on (ST-096, once the client sends the company): an article
   appears under the mapped company, **as a draft**, with one upload per included screenshot.
4. Record here, dated: whether `draft` was honoured, whether uploads appeared on the article, and what
   the article's `url` looked like. Correct `ScriptedHudu`'s recorded shapes from what you saw.

## The recorded shapes

`backend/tests/ScreenTail.Api.Tests/Providers/Hudu/ScriptedHudu.cs` answers the routes above with the
fields the provider reads, written from the API documentation. `DocProviderContract` and the provider's
own tests run against it.
