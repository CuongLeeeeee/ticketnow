\# TicketNow



Hệ thống bán vé sự kiện theo kiến trúc microservices, xây dựng bằng .NET 8.

Trọng tâm là bài toán flash sale: hàng nghìn người tranh nhau một lượng vé

có hạn trong vài giây, không được phép bán vượt, không được mất đơn.



!\[Trace trong Jaeger](docs/images/jaeger-trace.png)



\## Kết quả đo được



| Chỉ số | Giá trị |

|---|---|

| 200 request song song tranh 50 vé | bán đúng 50, 0 oversell |

| p95 thời gian đặt vé | <điền số của bạn> ms |

| Mất toàn bộ Redis | tồn kho tự khôi phục từ Postgres khi khởi động |

| RabbitMQ sập khi đang đặt vé | 0 event bị mất, tự đồng bộ khi bật lại |



\## Kiến trúc



```mermaid

flowchart TD

&#x20;   C\[Client] --> GW\[API gateway · YARP]

&#x20;   GW --> ID\[Identity]

&#x20;   GW --> CAT\[Catalog]

&#x20;   GW --> BK\[Booking]

&#x20;   GW --> PAY\[Payment]

&#x20;   GW --> NOT\[Notification]

&#x20;   BK <--> R\[(Redis)]

&#x20;   ID --> MQ{{RabbitMQ}}

&#x20;   CAT --> MQ

&#x20;   BK --> MQ

&#x20;   PAY --> MQ

&#x20;   MQ --> BK

&#x20;   MQ --> PAY

&#x20;   MQ --> NOT

&#x20;   NOT -.SignalR.-> C

```



Mỗi service có database Postgres riêng, không service nào truy vấn database

của service khác. Dữ liệu chia sẻ được đồng bộ bằng event.



\## Chạy thử



```bash

cp .env.example .env

docker compose --profile app up -d --build

```



| Thành phần | Địa chỉ |

|---|---|

| API gateway | http://localhost:5000 |

| Trang test realtime | http://localhost:5005 |

| Jaeger | http://localhost:16686 |

| RabbitMQ | http://localhost:15672 (ticketnow/ticketnow) |

| MailHog | http://localhost:8025 |



Tài khoản admin: `admin@ticketnow.local` / giá trị `SEED\_ADMIN\_PASSWORD` trong `.env`.



\## Những vấn đề kỹ thuật đã giải quyết



\*\*Chống oversell.\*\* Tồn kho là counter trên Redis, trừ bằng Lua script để việc

kiểm tra và trừ diễn ra nguyên tử. Cách làm ngây thơ (GET rồi DECRBY ở phía

ứng dụng) để lọt race condition ngay ở mức vài chục request song song.

Postgres có `CHECK (sold <= total)` làm chốt chặn cuối.



\*\*Không mất event (outbox pattern).\*\* Thay đổi nghiệp vụ và event được ghi

trong cùng một transaction. Một background service đọc bảng outbox bằng

`FOR UPDATE SKIP LOCKED` rồi publish lên RabbitMQ. Broker sập thì event nằm

yên trong database chờ gửi lại.



\*\*Xử lý đúng một lần (inbox pattern).\*\* Outbox đảm bảo at-least-once nên

consumer có thể nhận trùng. Mỗi consumer ghi `message\_id` vào bảng inbox trong

cùng transaction với thay đổi nghiệp vụ; message trùng bị bỏ qua.



\*\*Side effect ngoài database.\*\* Trả vé về Redis và gửi email không nằm trong

transaction Postgres được, nên cơ chế chống trùng đặt ngay tại Redis bằng

`SET NX` trong chính Lua script trả vé.



\*\*Saga có bước bù trừ.\*\* Nếu tiền về đúng lúc đơn vừa hết hạn, hệ thống không

giao vé mà phát `PaymentRefundRequested` để hoàn tiền tự động.



\*\*Idempotency.\*\* Endpoint đặt vé yêu cầu header `Idempotency-Key`; gửi lại

cùng key trả về đúng đơn cũ kèm header `Idempotent-Replayed`.



\*\*Trace liền mạch qua message queue.\*\* `traceparent` được lưu cùng event trong

bảng outbox và khôi phục lúc publish, nên một trace trong Jaeger chạy suốt từ

request HTTP qua RabbitMQ tới consumer ở service khác.



\## Công nghệ



.NET 8 · PostgreSQL · Redis · RabbitMQ (MassTransit) · YARP · SignalR ·

OpenTelemetry + Jaeger · Testcontainers · k6 · Docker Compose · GitHub Actions

