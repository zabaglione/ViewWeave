#if defined(LKG_WORKER_TRANSPORT_TESTING)
static void TestPersistentHostThreadReuse() {
    void* first = nullptr;
    BeginContext(&first);
    const uint32_t firstThread = lkg_worker_test_thread_id(first);
    Check(firstThread != 0, "persistent host first thread id");
    EndContext(first);

    void* second = nullptr;
    BeginContext(&second);
    const uint32_t secondThread = lkg_worker_test_thread_id(second);
    Check(secondThread == firstThread, "persistent host thread reuse");
    EndContext(second);
}

static void TestPersistentHostIdleQuit() {
    void* handle = nullptr;
    BeginContext(&handle);
    const DWORD threadId = lkg_worker_test_thread_id(handle);
    Check(threadId != 0, "persistent host idle quit thread id");
    EndContext(handle);
    Check(PostThreadMessageW(threadId, WM_QUIT, 0, 0) != FALSE, "post idle host quit");
    Sleep(50);
    void* rejected = lkg_worker_begin("test-runtime", "worker-transport-test", 0, nullptr);
    Check(rejected == nullptr, "idle host quit rejects new begin");
}
#endif
