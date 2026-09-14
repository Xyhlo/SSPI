/* Host adapter only; all network IO, ranges and checkpointing live in transfer/. */
static uint64_t gs_stream_clock(void) { return sceKernelGetProcessTime(); }
static int gs_download_stream(const char *partial)
{
    char destination[1100]; snprintf(destination, sizeof(destination), "%s", partial);
    size_t n = strlen(destination); if (n < 5 || strcmp(destination+n-5, ".part")) return -1; destination[n-5]=0;
retry_storage:
    while (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token) && !g_stop && !gs_canceled) {
        gs_write_status("storage-wait", "Reconnect the selected staging drive; download progress retained");
        sceKernelUsleep(500000);
    }
    if (g_stop || gs_canceled || sspi_xfer_init(gs_http)) return -1;
    int handle=sspi_xfer_start(gs_job.url,NULL,destination,gs_job.archive==1 ? "" : gs_job.title,
        gs_job.archive==1 ? "" : gs_job.content,gs_job.sha,gs_job.ranges);
    if(handle<0)return -1;
    int rc=-1, storage_lost=0;
    for(;;) {
        GsXferStatus status; if(sspi_xfer_poll(handle,&status))break;
        gs_done=status.done;gs_network_bytes=status.network_bytes;
        if(g_stop||gs_canceled){sspi_xfer_pause(handle);break;}
        if (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) {
            storage_lost=1;sspi_xfer_pause(handle);break;
        }
        if(gs_paused)sspi_xfer_pause(handle);else if(status.state==GS_PAUSED)sspi_xfer_resume(handle);
        if(status.state==GS_FAILED){gs_write_status("failed",status.error);break;}
        if(status.state==GS_COMPLETE){rc=0;break;}
        gs_write_status(gs_paused?"paused":status.state==GS_VALIDATING?"validating":"feeding",NULL);
        sceKernelUsleep(250000);
    }
    sspi_xfer_pause(handle);while(sspi_xfer_destroy(handle)==-2)sceKernelUsleep(50000);
    if (storage_lost && !g_stop && !gs_canceled) goto retry_storage;
    gs_transfer_verified=0;return rc;
}
