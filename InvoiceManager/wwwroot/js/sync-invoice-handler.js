/**
 * sync-invoice-handler.js
 * Quản lý Cửa sổ Đồng bộ hóa đơn tự động kết nối ngầm Cổng GOV (Không cần nhập mật khẩu hay Captcha)
 * FDI Accounting and Tax Pro v3.3.16
 */

(function () {
    var syncPollingTimer = null;
    var activeSyncJobId = null;

    // Helper: Định dạng ngày Date -> dd/MM/yyyy
    function formatDateDMY(d) {
        var day = String(d.getDate()).padStart(2, '0');
        var month = String(d.getMonth() + 1).padStart(2, '0');
        var year = d.getFullYear();
        return day + '/' + month + '/' + year;
    }

    // Helper: Lấy Verification Token
    function getCsrfToken() {
        return $('input[name="__RequestVerificationToken"]').val() || '';
    }

    // Mở modal Đồng bộ hóa đơn
    window.openSyncInvoiceModal = function () {
        // Đặt khoảng ngày mặc định: 30 ngày gần nhất
        var now = new Date();
        var past30Days = new Date();
        past30Days.setDate(now.getDate() - 30);

        if (!$('#syncFromDate').val()) {
            $('#syncFromDate').val(formatDateDMY(past30Days));
        }
        if (!$('#syncToDate').val()) {
            $('#syncToDate').val(formatDateDMY(now));
        }

        // Ẩn alert và progress nếu còn dư từ lần trước
        $('#syncModalAlert').addClass('d-none').text('');
        $('#syncLiveProgressBox').addClass('d-none');
        resetSyncButtons();

        // Mở Modal
        var modalEl = document.getElementById('modalSyncInvoices');
        if (modalEl) {
            var bsModal = bootstrap.Modal.getInstance(modalEl) || new bootstrap.Modal(modalEl);
            bsModal.show();
        }

        // Tải lịch sử đồng bộ
        loadSyncHistory();
    };

    // Tải lịch sử đồng bộ cho công ty hiện tại
    window.loadSyncHistory = function (silent) {
        if (!silent) {
            $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-4 text-muted"><div class="spinner-border spinner-border-sm text-secondary me-2" role="status"></div>Đang tải lịch sử đồng bộ...</td></tr>');
        }

        $.ajax({
            url: '/Import/GetSyncHistory',
            type: 'GET',
            cache: false,
            success: function (res) {
                if (res && res.success) {
                    if (res.companyName) {
                        $('#syncModalCompanyName').text(res.companyName);
                    }

                    var items = res.data || [];
                    if (items.length === 0) {
                        $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-4 text-muted">Không có dữ liệu</td></tr>');
                        return;
                    }

                    var html = '';
                    for (var i = 0; i < items.length; i++) {
                        var it = items[i];
                        var statusBadge = '';
                        if (it.status === 'Thành công') {
                            statusBadge = '<span class="badge bg-success-subtle text-success border border-success-subtle fw-medium px-2 py-1"><i class="bi bi-check-circle me-1"></i>Thành công</span>';
                        } else if (it.status === 'Cảnh báo') {
                            statusBadge = '<span class="badge bg-warning-subtle text-warning border border-warning-subtle fw-medium px-2 py-1"><i class="bi bi-exclamation-triangle me-1"></i>Cảnh báo</span>';
                        } else {
                            statusBadge = '<span class="badge bg-danger-subtle text-danger border border-danger-subtle fw-medium px-2 py-1"><i class="bi bi-x-circle me-1"></i>Thất bại</span>';
                        }

                        html += '<tr>' +
                            '<td class="text-center text-muted fw-semibold">' + (it.stt || (i + 1)) + '</td>' +
                            '<td>' + (it.fromDate || '-') + '</td>' +
                            '<td>' + (it.toDate || '-') + '</td>' +
                            '<td class="text-center fw-bold text-dark">' + (it.syncedCount || 0) + '</td>' +
                            '<td class="text-center">' + statusBadge + '</td>' +
                            '<td class="text-truncate" style="max-width: 280px;" title="' + (it.description || '') + '">' + (it.description || '-') + '</td>' +
                            '<td class="text-center text-muted small">' + (it.syncedAt || '-') + '</td>' +
                            '</tr>';
                    }

                    $('#tbodySyncHistory').html(html);
                } else {
                    $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-3 text-muted">Không có dữ liệu</td></tr>');
                }
            },
            error: function () {
                $('#tbodySyncHistory').html('<tr><td colspan="7" class="text-center py-3 text-danger"><i class="bi bi-exclamation-triangle me-1"></i>Lỗi khi tải lịch sử đồng bộ.</td></tr>');
            }
        });
    };

    // Thực hiện đồng bộ hóa đơn ngầm
    window.executeDirectGovSync = function () {
        var direction = $('input[name="syncInvoiceDirection"]:checked').val() || 'All';
        var category = $('input[name="syncInvoiceCategory"]:checked').val() || 'All';
        var fromDate = ($('#syncFromDate').val() || '').trim();
        var toDate = ($('#syncToDate').val() || '').trim();

        if (!fromDate || !toDate) {
            alert('Vui lòng chọn đầy đủ ngày bắt đầu và ngày kết thúc đồng bộ.');
            return;
        }

        // Khóa các nút và hiển thị tiến trình
        $('#btnSubmitSyncGov').prop('disabled', true).html('<span class="spinner-border spinner-border-sm me-1.5" role="status"></span> Đang đồng bộ...');
        $('#btnDeleteSyncData').prop('disabled', true);
        $('#btnCancelSync').prop('disabled', true);

        $('#syncLiveProgressBox').removeClass('d-none');
        $('#syncLiveProgressBar').css('width', '10%').removeClass('bg-danger bg-success').addClass('bg-teal').css('background-color', '#0d9488');
        $('#syncLivePercentBadge').text('10%');
        $('#syncLiveStageText').text('Đang tự động kết nối Cổng Tổng cục Thuế và giải Captcha AI ngầm...');
        $('#syncLiveStatsText').text('Khởi chạy tiến trình đồng bộ ngầm...');
        $('#syncModalAlert').addClass('d-none').text('');

        var payload = {
            FromDate: fromDate,
            ToDate: toDate,
            InvoiceType: direction,
            CategoryType: category
        };

        $.ajax({
            url: '/Import/AutoSync',
            type: 'POST',
            contentType: 'application/json',
            headers: { "RequestVerificationToken": getCsrfToken() },
            data: JSON.stringify(payload),
            success: function (res) {
                if (res.needSetup) {
                    resetSyncButtons();
                    $('#syncLiveProgressBox').addClass('d-none');
                    promptForInitialPassword(res.message);
                    return;
                }

                if (res.success && res.jobId) {
                    activeSyncJobId = res.jobId;
                    startSyncPolling(res.jobId);
                } else {
                    resetSyncButtons();
                    $('#syncLiveProgressBox').addClass('d-none');
                    $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                        .html('<i class="bi bi-exclamation-circle me-1"></i> ' + (res.message || 'Lỗi khi khởi chạy đồng bộ hóa đơn.'));
                }
            },
            error: function (xhr) {
                resetSyncButtons();
                $('#syncLiveProgressBox').addClass('d-none');
                $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                    .html('<i class="bi bi-exclamation-circle me-1"></i> Lỗi kết nối máy chủ khi gọi đồng bộ Cổng Thuế.');
            }
        });
    };

    // Theo dõi tiến trình đồng bộ ngầm
    function startSyncPolling(jobId) {
        if (syncPollingTimer) clearInterval(syncPollingTimer);

        syncPollingTimer = setInterval(function () {
            $.ajax({
                url: '/Import/RemoteStatus?jobId=' + encodeURIComponent(jobId),
                type: 'GET',
                cache: false,
                success: function (res) {
                    if (!res.success) return;

                    var percent = Math.max(10, Math.min(100, res.percent || 10));
                    $('#syncLiveProgressBar').css('width', percent + '%');
                    $('#syncLivePercentBadge').text(percent + '%');

                    if (res.stage) {
                        $('#syncLiveStageText').text(res.stage);
                    }

                    var stats = [];
                    if (res.totalFound > 0) stats.push('Tìm thấy ' + res.totalFound + ' HĐ');
                    if (res.newCount > 0) stats.push(res.newCount + ' mới');
                    if (res.duplicateCount > 0) stats.push(res.duplicateCount + ' đã đủ file');
                    if (res.failedCount > 0) stats.push(res.failedCount + ' lỗi');

                    if (stats.length > 0) {
                        $('#syncLiveStatsText').text(stats.join(' • '));
                    }

                    if (res.status === 'Completed') {
                        clearInterval(syncPollingTimer);
                        syncPollingTimer = null;
                        activeSyncJobId = null;

                        $('#syncLiveProgressBar').css('width', '100%').removeClass('bg-teal').addClass('bg-success');
                        $('#syncLivePercentBadge').text('100%').removeClass('bg-teal').addClass('bg-success');
                        $('#syncLiveStageText').text('Đồng bộ hoàn tất thành công!');

                        resetSyncButtons();
                        loadSyncHistory(true);

                        $('#syncModalAlert').removeClass('d-none alert-danger').addClass('alert-success')
                            .html('<i class="bi bi-check-circle-fill me-1"></i> Đồng bộ hóa đơn thành công! ' + (res.stage || ''));

                        // Tự động làm mới bảng hóa đơn trên màn hình chính nếu có
                        if (typeof window.applyInvoiceFilter === 'function') {
                            window.applyInvoiceFilter();
                        } else if (typeof window.reloadInvoicesTable === 'function') {
                            window.reloadInvoicesTable();
                        }
                    } else if (res.status === 'Failed') {
                        clearInterval(syncPollingTimer);
                        syncPollingTimer = null;
                        activeSyncJobId = null;

                        $('#syncLiveProgressBar').removeClass('bg-teal').addClass('bg-danger');
                        $('#syncLivePercentBadge').removeClass('bg-teal').addClass('bg-danger');
                        $('#syncLiveStageText').text('Tiến trình đồng bộ thất bại');

                        resetSyncButtons();
                        loadSyncHistory(true);

                        $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                            .html('<i class="bi bi-exclamation-triangle-fill me-1"></i> ' + (res.stage || 'Lỗi khi đồng bộ hóa đơn từ Cổng Thuế.'));
                    }
                },
                error: function () {
                    // Tiếp tục đợi
                }
            });
        }, 800);
    }

    // Xóa dữ liệu hóa đơn đã đồng bộ trong khoảng ngày đã chọn
    window.confirmDeleteSyncData = function () {
        var compName = $('#syncModalCompanyName').text() || 'công ty hiện tại';
        var fromDate = ($('#syncFromDate').val() || '').trim();
        var toDate = ($('#syncToDate').val() || '').trim();
        var direction = $('input[name="syncInvoiceDirection"]:checked').val() || 'All';

        var dirText = 'tất cả hóa đơn mua vào & bán ra';
        if (direction === 'BanRa') dirText = 'hóa đơn bán ra';
        if (direction === 'MuaVao') dirText = 'hóa đơn mua vào';

        var confirmMsg = 'Bạn có chắc chắn muốn XÓA DỮ LIỆU ĐÃ ĐỒNG BỘ (' + dirText + ') của ' + compName + ' trong khoảng thời gian từ ' + fromDate + ' đến ' + toDate + ' không?\n\nThao tác này sẽ xóa các hóa đơn và tệp tương ứng trong khoảng ngày này để bạn có thể đồng bộ lại sạch sẽ từ Cổng Thuế.';

        if (!confirm(confirmMsg)) {
            return;
        }

        $('#btnDeleteSyncData').prop('disabled', true).html('<span class="spinner-border spinner-border-sm me-1" role="status"></span> Đang xóa...');

        $.ajax({
            url: '/Import/DeleteSyncData',
            type: 'POST',
            contentType: 'application/json',
            headers: { "RequestVerificationToken": getCsrfToken() },
            data: JSON.stringify({
                FromDate: fromDate,
                ToDate: toDate,
                InvoiceType: direction
            }),
            success: function (res) {
                $('#btnDeleteSyncData').prop('disabled', false).html('<i class="bi bi-trash"></i> Xóa dữ liệu đã đồng bộ');
                if (res && res.success) {
                    $('#syncModalAlert').removeClass('d-none alert-danger').addClass('alert-success')
                        .html('<i class="bi bi-check-circle me-1"></i> ' + res.message);
                    loadSyncHistory(true);

                    // Làm mới bảng danh sách bên ngoài nếu có
                    if (typeof window.applyInvoiceFilter === 'function') {
                        window.applyInvoiceFilter();
                    } else if (typeof window.reloadInvoicesTable === 'function') {
                        window.reloadInvoicesTable();
                    }
                } else {
                    $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                        .html('<i class="bi bi-exclamation-circle me-1"></i> ' + (res.message || 'Lỗi khi xóa dữ liệu đã đồng bộ.'));
                }
            },
            error: function () {
                $('#btnDeleteSyncData').prop('disabled', false).html('<i class="bi bi-trash"></i> Xóa dữ liệu đã đồng bộ');
                $('#syncModalAlert').removeClass('d-none alert-success').addClass('alert-danger')
                    .html('<i class="bi bi-exclamation-circle me-1"></i> Lỗi kết nối khi gửi yêu cầu xóa dữ liệu.');
            }
        });
    };

    // Khi công ty chưa từng lưu mật khẩu Cổng Thuế một lần nào
    function promptForInitialPassword(msg) {
        var compName = $('#syncModalCompanyName').text() || 'doanh nghiệp';
        var promptMsg = 'Doanh nghiệp [' + compName + '] chưa lưu mật khẩu Cổng Thuế.\n\nVui lòng nhập mật khẩu Cổng Thuế 1 lần duy nhất để hệ thống lưu và tự động kết nối ngầm vĩnh viễn:';
        var pass = prompt(promptMsg, '');
        if (!pass) return;

        $.ajax({
            url: '/Import/SaveGovConfig',
            type: 'POST',
            contentType: 'application/json',
            headers: { "RequestVerificationToken": getCsrfToken() },
            data: JSON.stringify({
                Password: pass
            }),
            success: function (res) {
                if (res && res.success) {
                    alert('Đã lưu mật khẩu Cổng Thuế thành công! Hệ thống sẽ tự động đồng bộ ngay bây giờ.');
                    executeDirectGovSync();
                } else {
                    alert(res.message || 'Lỗi khi lưu mật khẩu Cổng Thuế.');
                }
            },
            error: function () {
                alert('Lỗi kết nối khi lưu mật khẩu Cổng Thuế.');
            }
        });
    }

    function resetSyncButtons() {
        $('#btnSubmitSyncGov').prop('disabled', false).html('<i class="bi bi-arrow-repeat me-1"></i> Đồng bộ');
        $('#btnDeleteSyncData').prop('disabled', false);
        $('#btnCancelSync').prop('disabled', false);
    }
})();
