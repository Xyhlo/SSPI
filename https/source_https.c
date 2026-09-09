#include <curl/curl.h>
#include <mbedtls/platform_time.h>
#include <mbedtls/entropy.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <strings.h>
#include <orbis/libkernel.h>
#include <fcntl.h>
#include <time.h>
#include <unistd.h>

/* The managed caller serializes requests, including global TLS initialization. */
static int initialized;
static time_t request_utc;
static struct timespec request_started;
static void trace(const char *stage)
{
    int file = sceKernelOpen("/data/GameSearch/source-https-native.log", O_WRONLY | O_CREAT | O_APPEND, 0600);
    if (file >= 0) {
        sceKernelWrite(file, stage, strlen(stage));
        sceKernelWrite(file, "\n", 1);
        sceKernelClose(file);
    }
}

static time_t certificate_time(time_t *result)
{
    struct timespec now;
    time_t value = request_utc;
    if (clock_gettime(CLOCK_MONOTONIC, &now) == 0)
        value += now.tv_sec - request_started.tv_sec;
    if (result) *result = value;
    return value;
}

int mbedtls_hardware_poll(void *context, unsigned char *output, size_t length, size_t *written)
{
    (void)context;
    *written = 0;
    while (*written < length) {
        size_t count = length - *written;
        if (count > 256) count = 256;
        if (getentropy(output + *written, count) != 0)
            return MBEDTLS_ERR_ENTROPY_SOURCE_FAILED;
        *written += count;
    }
    return 0;
}

struct response {
    unsigned char *data;
    size_t size, capacity;
    char *location;
    size_t location_capacity;
};

static size_t body_write(char *data, size_t size, size_t count, void *opaque)
{
    struct response *response = opaque;
    if (size && count > SIZE_MAX / size) return 0;
    size_t length = size * count;
    if (length > response->capacity - response->size) return 0;
    memcpy(response->data + response->size, data, length);
    response->size += length;
    return length;
}

static size_t header_write(char *data, size_t size, size_t count, void *opaque)
{
    struct response *response = opaque;
    if (size && count > SIZE_MAX / size) return 0;
    size_t length = size * count;
    if (length >= 9 && strncasecmp(data, "Location:", 9) == 0) {
        size_t begin = 9, end = length;
        while (begin < end && (data[begin] == ' ' || data[begin] == '\t')) begin++;
        while (end > begin && (data[end-1] == '\r' || data[end-1] == '\n' || data[end-1] == ' ')) end--;
        if (end - begin >= response->location_capacity) return 0;
        memcpy(response->location, data + begin, end - begin);
        response->location[end - begin] = 0;
    }
    return length;
}

#ifdef __cplusplus
extern "C" {
#endif

__attribute__((visibility("default"), used))
int gs_https_get(const char *ca, const char *url, const char *referer, const char *user_agent,
                 int timeout_ms, int64_t utc, unsigned char *body, int capacity,
                 int *length, int *status, char *location, int location_capacity,
                 char *error, int error_capacity)
{
    if (!ca || !url || !body || capacity <= 0 || !length || !status ||
        !location || location_capacity < 2 || !error || error_capacity < 2) return -1;
    *length = 0; *status = 0; location[0] = 0; error[0] = 0;
    trace("request start source-only");
    request_utc = (time_t)utc;
    if (clock_gettime(CLOCK_MONOTONIC, &request_started) != 0) {
        snprintf(error, error_capacity, "Cannot read monotonic clock");
        return -3;
    }
    if (!initialized) {
        trace("TLS initialization");
        mbedtls_platform_set_time(certificate_time);
        CURLcode init = curl_global_init(CURL_GLOBAL_DEFAULT);
        if (init != CURLE_OK) {
            snprintf(error, error_capacity, "curl init: %s", curl_easy_strerror(init));
            return (int)init;
        }
        initialized = 1;
    }
    trace("create request");
    CURL *curl = curl_easy_init();
    if (!curl) { snprintf(error, error_capacity, "Cannot allocate HTTPS request"); return -4; }
    struct response response = {body, 0, (size_t)capacity, location, (size_t)location_capacity};
    char detail[CURL_ERROR_SIZE] = {0};
    CURLcode rc;
#define SET(option, value) do { rc = curl_easy_setopt(curl, option, value); if (rc != CURLE_OK) goto done; } while (0)
    SET(CURLOPT_URL, url);
    SET(CURLOPT_PROTOCOLS_STR, "http,https");
    SET(CURLOPT_FOLLOWLOCATION, 0L);
    /*
     * This bridge is never used for package/debrid transfers. It exists only
     * for GameSource discovery on jailbroken consoles whose Sony trust/time
     * state is intentionally unavailable. Keep the policy isolated here;
     * NativeHttp remains the verified transport for all package bytes.
     */
    SET(CURLOPT_SSL_VERIFYPEER, 0L);
    SET(CURLOPT_SSL_VERIFYHOST, 0L);
    (void)ca;
    SET(CURLOPT_SSLVERSION, CURL_SSLVERSION_TLSv1_2);
    SET(CURLOPT_HTTP_VERSION, CURL_HTTP_VERSION_1_1);
    SET(CURLOPT_CONNECTTIMEOUT_MS, 15000L);
    SET(CURLOPT_TIMEOUT_MS, (long)(timeout_ms > 0 ? timeout_ms : 45000));
    SET(CURLOPT_NOSIGNAL, 1L);
    SET(CURLOPT_USERAGENT, user_agent);
    SET(CURLOPT_ACCEPT_ENCODING, "identity");
    if (referer && *referer) SET(CURLOPT_REFERER, referer);
    SET(CURLOPT_ERRORBUFFER, detail);
    SET(CURLOPT_WRITEFUNCTION, body_write);
    SET(CURLOPT_WRITEDATA, &response);
    SET(CURLOPT_HEADERFUNCTION, header_write);
    SET(CURLOPT_HEADERDATA, &response);
    trace("perform request");
    rc = curl_easy_perform(curl);
    trace("request returned");
done:
    {
        long http_status = 0;
        curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &http_status);
        *status = (int)http_status;
        *length = (int)response.size;
        if (rc != CURLE_OK)
            snprintf(error, error_capacity, "curl %d: %s", (int)rc, *detail ? detail : curl_easy_strerror(rc));
    }
    curl_easy_cleanup(curl);
    return (int)rc;
}

#ifdef __cplusplus
}
#endif
