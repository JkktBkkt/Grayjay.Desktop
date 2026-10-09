#include "content_decryption_module.h"
#include <unistd.h>
#include <dlfcn.h>
#include <sys/select.h>
#include <chrono>
#include <iostream>
#include <sstream>
#include <vector>
#include <algorithm>
#include <limits>
#include <stdexcept>
#include <memory>
extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/encryption_info.h>
#include <libavutil/base64.h>
}

using Clock = std::chrono::steady_clock;
constexpr size_t MaxPayload = 36 * 1024 * 1024;
std::string quote(const std::string& value) {
    std::string result = "\"";
    for (unsigned char c : value) {
        if (c == '\\' || c == '"') { result += '\\'; result += c; }
        else if (c < 32) { char escaped[7]; snprintf(escaped, sizeof(escaped), "\\u%04x", c); result += escaped; }
        else result += c;
    }
    return result + '"';
}
std::string encode(const void* input, size_t length) {
    if (length > MaxPayload) throw std::runtime_error("Response exceeds playback limits");
    std::string result(AV_BASE64_SIZE(length), '\0');
    av_base64_encode(result.data(), result.size(), static_cast<const uint8_t*>(input), length);
    result.resize(result.find('\0')); return result;
}
std::vector<uint8_t> decode(const std::string& input) {
    if (input.size() > MaxPayload * 4 / 3 || input.size() % 4) throw std::runtime_error("Invalid base64 payload");
    std::vector<uint8_t> result(input.size() / 4 * 3 + 1);
    int length = av_base64_decode(result.data(), input.c_str(), result.size());
    if (length < 0) throw std::runtime_error("Invalid base64 payload");
    result.resize(length); return result;
}
void emit(const std::string& json) { std::cout << json << std::endl; }

class Buffer : public cdm::Buffer {
    std::vector<uint8_t> storage; uint32_t size = 0;
public:
    explicit Buffer(uint32_t capacity) : storage(capacity) {}
    void Destroy() override { delete this; }
    uint32_t Capacity() const override { return storage.size(); }
    uint8_t* Data() override { return storage.data(); }
    void SetSize(uint32_t value) override { if (value > storage.size()) throw std::runtime_error("Invalid CDM buffer size"); size = value; }
    uint32_t Size() const override { return size; }
};
class Block : public cdm::DecryptedBlock {
    cdm::Buffer* buffer = nullptr; int64_t timestamp = 0;
public:
    ~Block() { if (buffer) buffer->Destroy(); }
    void SetDecryptedBuffer(cdm::Buffer* value) override { buffer = value; }
    cdm::Buffer* DecryptedBuffer() override { return buffer; }
    void SetTimestamp(int64_t value) override { timestamp = value; }
    int64_t Timestamp() const override { return timestamp; }
};

class Host : public cdm::Host_10 {
    struct Timer { Clock::time_point due; void* context; };
    std::vector<Timer> timers;
public:
    cdm::ContentDecryptionModule_10* cdm = nullptr;
    std::string keyStatuses;
    cdm::Buffer* Allocate(uint32_t bytes) override { return bytes <= MaxPayload ? new Buffer(bytes) : nullptr; }
    void SetTimer(int64_t delay, void* context) override { timers.push_back({Clock::now() + std::chrono::milliseconds(std::max<int64_t>(0, delay)), context}); }
    cdm::Time GetCurrentWallTime() override { return std::chrono::duration<double>(std::chrono::system_clock::now().time_since_epoch()).count(); }
    void OnInitialized(bool success) override { emit("{\"event\":\"initialized\",\"protocol\":3,\"success\":" + std::string(success ? "true}" : "false}")); }
    void OnResolveKeyStatusPromise(uint32_t id, cdm::KeyStatus status) override { emit("{\"event\":\"resolved\",\"id\":" + std::to_string(id) + ",\"status\":" + std::to_string(status) + "}"); }
    void OnResolveNewSessionPromise(uint32_t id, const char* session, uint32_t length) override {
        emit("{\"event\":\"created\",\"id\":" + std::to_string(id) + ",\"session\":" + quote(encode(session, length)) + "}");
    }
    void OnResolvePromise(uint32_t id) override { emit("{\"event\":\"resolved\",\"id\":" + std::to_string(id) + "}"); }
    void OnRejectPromise(uint32_t id, cdm::Exception exception, uint32_t code, const char* message, uint32_t length) override {
        emit("{\"event\":\"rejected\",\"id\":" + std::to_string(id) + ",\"exception\":" + std::to_string(exception) +
            ",\"code\":" + std::to_string(code) + ",\"message\":" + quote(std::string(message, length)) + "}");
    }
    void OnSessionMessage(const char* session, uint32_t sessionLength, cdm::MessageType type, const char* message, uint32_t length) override {
        emit("{\"event\":\"message\",\"session\":" + quote(encode(session, sessionLength)) + ",\"type\":" + std::to_string(type) + ",\"data\":" + quote(encode(message, length)) + "}");
    }
    void OnSessionKeysChange(const char* session, uint32_t length, bool, const cdm::KeyInformation* keys, uint32_t count) override {
        std::string statuses; for (uint32_t i = 0; i < count; ++i) statuses += (i ? "," : "") + std::to_string(keys[i].status);
        keyStatuses.clear();
        for (uint32_t i = 0; i < count; ++i) keyStatuses += (i ? "," : "") + encode(keys[i].key_id, keys[i].key_id_size) + ":" + std::to_string(keys[i].status);
        emit("{\"event\":\"keys\",\"session\":" + quote(encode(session, length)) + ",\"statuses\":[" + statuses + "]}");
    }
    void OnExpirationChange(const char* session, uint32_t length, cdm::Time expiry) override {
        emit("{\"event\":\"expiration\",\"session\":" + quote(encode(session, length)) + ",\"expiry\":" + std::to_string(expiry) + "}");
    }
    void OnSessionClosed(const char* session, uint32_t length) override { emit("{\"event\":\"closed\",\"session\":" + quote(encode(session, length)) + "}"); }
    void SendPlatformChallenge(const char*, uint32_t, const char*, uint32_t) override { cdm::PlatformChallengeResponse response{}; cdm->OnPlatformChallengeResponse(response); }
    void EnableOutputProtection(uint32_t) override {}
    void QueryOutputProtectionStatus() override { cdm->OnQueryOutputProtectionStatus(cdm::kQueryFailed, 0, 0); }
    void OnDeferredInitializationDone(cdm::StreamType, cdm::Status) override {}
    cdm::FileIO* CreateFileIO(cdm::FileIOClient*) override { return nullptr; }
    void RequestStorageId(uint32_t version) override { cdm->OnStorageId(version, nullptr, 0); }
    void tick() {
        auto now = Clock::now(); std::vector<void*> ready;
        for (auto it = timers.begin(); it != timers.end();) {
            if (it->due <= now) { ready.push_back(it->context); it = timers.erase(it); } else ++it;
        }
        for (void* context : ready) cdm->TimerExpired(context);
    }
    void fragment(uint32_t id, const std::string& payload) {
        auto wire = decode(payload);
        if (wire.size() < 4) throw std::runtime_error("Truncated fragment request");
        size_t init = 0; for (int i = 0; i < 4; ++i) init = (init << 8) | wire[i];
        if (init > wire.size() - 4) throw std::runtime_error("Invalid fragment initialization size");
        std::vector<uint8_t> data(wire.begin() + 4, wire.end());
        char path[] = "/tmp/grayjay-fragment-XXXXXX";
        int fd = mkstemp(path); if (fd < 0) throw std::runtime_error("Could not create fragment input");
        size_t written = 0;
        while (written < data.size()) {
            auto count = write(fd, data.data() + written, data.size() - written);
            if (count <= 0) { close(fd); unlink(path); throw std::runtime_error("Could not write fragment input"); }
            written += count;
        }
        close(fd);
        AVFormatContext* format = nullptr;
        int opened = avformat_open_input(&format, path, nullptr, nullptr); unlink(path);
        if (opened < 0) throw std::runtime_error("Unsupported encrypted media fragment");
        AVPacket* packet = av_packet_alloc();
        if (!packet) { avformat_close_input(&format); throw std::bad_alloc(); }
        int decrypted = 0, failed = 0;
        std::string firstFailure;
        try {
            int result;
            while ((result = av_read_frame(format, packet)) >= 0) {
                size_t sideSize = 0; auto* side = av_packet_get_side_data(packet, AV_PKT_DATA_ENCRYPTION_INFO, &sideSize);
                auto* encryption = side ? av_encryption_info_get_side_data(side, sideSize) : nullptr;
                std::unique_ptr<AVEncryptionInfo, decltype(&av_encryption_info_free)> encryptionOwner(encryption, &av_encryption_info_free);
                if (encryption) {
                    cdm::InputBuffer_2 input{};
                    input.data = packet->data; input.data_size = packet->size; input.key_id = encryption->key_id; input.key_id_size = encryption->key_id_size;
                    input.iv = encryption->iv; input.iv_size = encryption->iv_size;
                    if (packet->pts != AV_NOPTS_VALUE)
                        input.timestamp = av_rescale_q(packet->pts, format->streams[packet->stream_index]->time_base, AVRational{1, 1000000});
                    if (encryption->scheme != 0x63626373 && encryption->scheme != 0x63656e63)
                        throw std::runtime_error("Unsupported MP4 encryption scheme");
                    input.encryption_scheme = encryption->scheme == 0x63626373 ? cdm::EncryptionScheme::kCbcs : cdm::EncryptionScheme::kCenc;
                    input.pattern = {encryption->crypt_byte_block, encryption->skip_byte_block};
                    std::vector<cdm::SubsampleEntry> subsamples;
                    uint64_t protectedBytes = encryption->subsample_count ? 0 : packet->size;
                    uint64_t sampleBytes = 0;
                    for (uint32_t i = 0; i < encryption->subsample_count; ++i) {
                        auto sample = encryption->subsamples[i];
                        subsamples.push_back({sample.bytes_of_clear_data, sample.bytes_of_protected_data});
                        protectedBytes += sample.bytes_of_protected_data;
                        sampleBytes += static_cast<uint64_t>(sample.bytes_of_clear_data) + sample.bytes_of_protected_data;
                    }
                    if (encryption->subsample_count && sampleBytes != static_cast<uint32_t>(packet->size)) {
                        throw std::runtime_error("Invalid encrypted sample boundaries");
                    }

                    if (!protectedBytes) { av_packet_unref(packet); continue; }
                    input.subsamples = subsamples.data(); input.num_subsamples = subsamples.size();
                    std::vector<uint8_t> avc;
                    size_t configuration = 0;
                    auto* parameters = format->streams[packet->stream_index]->codecpar;
                    if (parameters->codec_id == AV_CODEC_ID_H264 && parameters->extradata_size >= 5 &&
                        parameters->extradata[0] == 1 && (parameters->extradata[4] & 3) == 3 &&
                        !subsamples.empty() && subsamples.front().clear_bytes >= 4) {

                        avc = {0, 0, 0, 2, 9, 240};
                        configuration = avc.size();
                        avc.insert(avc.end(), packet->data, packet->data + packet->size);
                        subsamples.front().clear_bytes += configuration;
                        input.data = avc.data(); input.data_size = avc.size();
                        input.subsamples = subsamples.data(); input.num_subsamples = subsamples.size();
                    }
                    tick();
                    Block block; auto status = cdm->Decrypt(input, &block); auto* plain = block.DecryptedBuffer();
                    if (status == cdm::kSuccess && plain && plain->Size() == packet->size + configuration && packet->pos >= static_cast<int64_t>(init) &&
                        static_cast<uint64_t>(packet->pos) + packet->size <= data.size()) {
                        std::copy(plain->Data() + configuration, plain->Data() + plain->Size(), data.begin() + packet->pos); ++decrypted;
                    } else {
                        ++failed;
                        if (firstFailure.empty()) firstFailure = " (status=" + std::to_string(status) +
                            ", position=" + std::to_string(packet->pos) + ", init=" + std::to_string(init) +
                            ", packet=" + std::to_string(packet->size) + ", output=" + std::to_string(plain ? plain->Size() : 0) +
                            ", fragment=" + std::to_string(data.size() - init) + ", protected=" + std::to_string(protectedBytes) +
                            ", keyId=" + encode(encryption->key_id, encryption->key_id_size) + ", statuses=" + keyStatuses + ")";
                    }
                }

                av_packet_unref(packet);
            }
            if (result != AVERROR_EOF) throw std::runtime_error("Encrypted fragment demuxing failed");
            if (failed) throw std::runtime_error("Widevine could not decrypt " + std::to_string(failed) + " media samples" + firstFailure);
            emit("{\"event\":\"fragment\",\"id\":" + std::to_string(id) + ",\"samples\":" + std::to_string(decrypted) +
                ",\"data\":" + quote(encode(data.data() + init, data.size() - init)) + "}");
        } catch (...) { av_packet_free(&packet); avformat_close_input(&format); throw; }
        av_packet_free(&packet); avformat_close_input(&format);
    }
};

void* getHost(int version, void* data) { return version == 10 ? static_cast<cdm::Host_10*>(static_cast<Host*>(data)) : nullptr; }
int main(int argc, char** argv) {
    if (argc != 2) return 2;
    void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (!library) { std::cerr << dlerror() << std::endl; return 3; }
    auto initialize = reinterpret_cast<void (*)()>(dlsym(library, "InitializeCdmModule_4"));
    auto create = reinterpret_cast<decltype(&CreateCdmInstance)>(dlsym(library, "CreateCdmInstance"));
    auto deinitialize = reinterpret_cast<void (*)()>(dlsym(library, "DeinitializeCdmModule"));
    if (!initialize || !create) return 4;
    initialize(); Host host;
    host.cdm = static_cast<cdm::ContentDecryptionModule_10*>(create(10, "com.widevine.alpha", 18, getHost, &host));
    if (!host.cdm) return 5;
    host.cdm->Initialize(false, false, false);
    std::string pending; bool running = true; auto lastInput = Clock::now();
    while (running && Clock::now() - lastInput < std::chrono::seconds(30)) {
        host.tick(); fd_set ready; FD_ZERO(&ready); FD_SET(0, &ready); timeval timeout{0, 10000};
        if (select(1, &ready, nullptr, nullptr, &timeout) <= 0) continue;
        char buffer[65536]; auto length = read(0, buffer, sizeof(buffer)); if (length <= 0) break;
        lastInput = Clock::now(); pending.append(buffer, length);
        if (pending.size() > MaxPayload * 4 / 3 + 1024) break;
        size_t newline;
        while ((newline = pending.find('\n')) != std::string::npos) {
            std::istringstream line(pending.substr(0, newline)); pending.erase(0, newline + 1);
            std::string command, first, second; uint32_t id = 0;
            line >> command;
            if (command == "QUIT") { running = false; break; }
            if (command == "PING") continue;
            try {
                if (!(line >> id >> first) || !id) throw std::runtime_error("Invalid playback command");
                if (command == "SESSION") { auto data = decode(first); host.cdm->CreateSessionAndGenerateRequest(id, cdm::kTemporary, cdm::kCenc, data.data(), data.size()); }
                else if (command == "CERT") { auto data = decode(first); host.cdm->SetServerCertificate(id, data.data(), data.size()); }
                else if (command == "UPDATE") {
                    if (!(line >> second)) throw std::runtime_error("Missing license response");
                    auto session = decode(first), data = decode(second);
                    host.cdm->UpdateSession(id, reinterpret_cast<const char*>(session.data()), session.size(), data.data(), data.size());
                }
                else if (command == "CLOSE") {
                    auto session = decode(first);
                    host.cdm->CloseSession(id, reinterpret_cast<const char*>(session.data()), session.size());
                }
                else if (command == "FRAGMENT") host.fragment(id, first);
                else throw std::runtime_error("Unknown playback command");
            } catch (const std::exception& error) { emit("{\"event\":\"error\",\"id\":" + std::to_string(id) + ",\"message\":" + quote(error.what()) + "}"); }
        }
    }
    host.cdm->Destroy(); if (deinitialize) deinitialize(); dlclose(library);
    return 0;
}
